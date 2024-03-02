﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OrderRice.Exceptions;
using OrderRice.Interfaces;
using OrderRice.Persistence;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using System.Linq;
using System.Text;
using Color = SixLabors.ImageSharp.Color;

namespace OrderRice.Services
{
    public class OrderService : IOrderService
    {
        private readonly HttpClient _httpGoogleClient;
        private readonly GithubService _githubService;
        private readonly GoogleSheetContext _googleSheetContext;
        private readonly ILogger<OrderService> _logger;
        private readonly string spreadSheetId;
        private readonly string BASE_IMAGE_URL;
        private const int SIZE_LIST = 23;
        private const int FONT_SIZE = 40;

        public OrderService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            GithubService githubService,
            GoogleSheetContext googleSheetContext,
            ILogger<OrderService> logger)
        {
            _httpGoogleClient = httpClientFactory.CreateClient("google_sheet_client");
            _githubService = githubService;
            _logger = logger;
            spreadSheetId = configuration["SpreadSheetId"];
            BASE_IMAGE_URL = configuration["BASE_IMAGE"];
            _googleSheetContext = googleSheetContext;
        }

        private async Task<string> FindSheetId(DateTime dateTime)
        {
            var response = await _httpGoogleClient.GetAsync($"/v4/spreadsheets/{spreadSheetId}");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            var jObject = JsonConvert.DeserializeObject<JObject>(body);
            var jArraySheets = (JArray)jObject["sheets"];
            foreach (var sheet in jArraySheets)
            {
                var title = sheet.SelectToken("properties.title").ToString();
                if (title.Equals($"T{dateTime:M/yyyy}"))
                {
                    return sheet.SelectToken("properties.sheetId").ToString();
                }
            }
            return string.Empty;
        }

        private async Task<List<List<string>>> GetSpreadSheetData(string sheetId)
        {
            var payload = new
            {
                dataFilters = new[] { new { gridRange = new { sheetId, startColumnIndex = 1 } } },
                majorDimension = "COLUMNS"
            };
            var json = JsonConvert.SerializeObject(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpGoogleClient.PostAsync($"/v4/spreadsheets/{spreadSheetId}/values:batchGetByDataFilter", httpContent);
            var body = await response.Content.ReadAsStringAsync();
            dynamic jsonConvert = JsonConvert.DeserializeObject(body);
            var listResult = jsonConvert.valueRanges[0].valueRange.values;
            return JsonConvert.DeserializeObject<List<List<string>>>(Convert.ToString(listResult));
        }

        private async Task<bool> WriteSpreadSheet(DateTime startDate, int startColumnIndex, int endColumnIndex, int rowIndex, string sheetId, string text = "x")
        {
            string[][] values = new string[1][];
            int totalItem = (endColumnIndex - startColumnIndex);
            for (int i = 0; i < totalItem; i++)
            {
                if (i == 0)
                {
                    values[i] = new string[totalItem];
                }
                var isWeeken = startDate.DayOfWeek == DayOfWeek.Sunday || startDate.DayOfWeek == DayOfWeek.Saturday;
                values[0][i] = !isWeeken ? text : string.Empty;
                startDate = startDate.AddDays(1);
            }

            var payload = new
            {
                valueInputOption = "RAW",
                data = new[]
                {
                    new
                    {
                        dataFilter = new {
                            gridRange = new {
                                    sheetId,
                                    startColumnIndex,
                                    endColumnIndex,
                                    startRowIndex = rowIndex,
                                    endRowIndex = rowIndex + totalItem,
                            }
                        },
                        values
                    }
                }
            };

            var json = JsonConvert.SerializeObject(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpGoogleClient.PostAsync($"/v4/spreadsheets/{spreadSheetId}/values:batchUpdateByDataFilter", httpContent);
            return response.IsSuccessStatusCode;
        }

        private int GetIndexDateColumn(List<List<string>> array2D, DateTime dateTime)
        {
            int currentDateItem = 0;
            var currentDate = dateTime.ToString("dd/MM/yyyy");
            for (int i = 0; i < array2D.Count; i++)
            {
                if (currentDate.Contains(array2D[i][0]))
                {
                    currentDateItem = i;
                    break;
                }
            }
            return currentDateItem;
        }

        private async Task<(List<(string, string)>, string, string)> ProcessCreateImage(List<List<string>> datas, int indexCurrentDate, Image<Rgba32> baseImage)
        {
            var blackListUsers = _googleSheetContext.Users.Where(x => x.IsBlacklist == true).Select(x => x.FullName).ToList();

            bool IsContain(List<string> blackListUsers, string name)
            {
                foreach (var fullName in blackListUsers)
                {
                    if (name.Contains(fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            void AddList(List<string> floor16, List<string> floor19, string name)
            {
                if (IsContain(blackListUsers, name))
                {
                    return;
                }

                if (name.Contains("19"))
                {
                    floor19.Add(name);
                }
                else
                {
                    floor16.Add(name);
                }
            }

            Dictionary<int, string> deptMap = new();
            List<string> floor16 = new();
            List<string> floor19 = new();
            try
            {
                deptMap = await UnPaidList();
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception: {Message}", ex.Message);
            }

            Dictionary<string, string> registerLunchTodays = new();
            string statusPaid;
            // Get the list registration lunch today
            for (int i = 0; i < datas[indexCurrentDate].Count; i++)
            {
                statusPaid = "";
                if (datas[indexCurrentDate][i].Equals("x", StringComparison.OrdinalIgnoreCase))
                {
                    var name = datas[0][i];
                    AddList(floor16, floor19, name);
                    if (deptMap.ContainsKey(i))
                    {
                        statusPaid = "Nợ";
                        AddList(floor16, floor19, name);
                        AddList(floor16, floor19, name);
                    }
                    registerLunchTodays.Add(name, statusPaid);
                }
            }

            List<(string, string)> listRegister = new();
            var registerLunchTodaysChunks = registerLunchTodays.Chunk(SIZE_LIST);
            int countImage = 1;
            var font = new Font(SystemFonts.Get("Arial"), FONT_SIZE, FontStyle.Regular);
            var color = new Color(Rgba32.ParseHex("#000000"));
            int index = 1;
            foreach (var list in registerLunchTodaysChunks)
            {
                int step = 0;
                int start = 535;
                var image = baseImage.Clone();
                foreach (var l in list)
                {
                    image.Mutate(ctx => ctx.DrawText(l.Value, font, color, location: new PointF(1140, start + step)));
                    image.Mutate(ctx => ctx.DrawText($"{index}", font, color, location: new PointF(index < 10 ? 252 : 240, start + step)));
                    image.Mutate(ctx => ctx.DrawText(l.Key, font, color, location: new PointF(390, start + step)));
                    step += 59;
                    index++;
                }

                var response = await _githubService
                                        .UploadImageAsync(image.ToBase64String(PngFormat.Instance)
                                        .Split(';')[1]
                                        .Replace("base64,", ""), "list");
                listRegister.Add(new(response.Content.DownloadUrl, $"Ảnh {countImage++}"));
            }

            // Random user pick ticket for lunch
            Random random = new();
            int randomIndexFloor16 = random.Next(0, floor16.Count);
            int randomIndexFloor19 = random.Next(0, floor19.Count);

            return new(listRegister, floor16.Count > 0 ? floor16[randomIndexFloor16] : string.Empty, floor19.Count > 0 ? floor19[randomIndexFloor19] : string.Empty);
        }

        public async Task<(List<(string, string)>, string, string)> CreateOrderListImage()
        {
            var sheetId = await FindSheetId(DateTime.Now);
            if (string.IsNullOrEmpty(sheetId))
            {
                throw new OrderServiceException(ErrorMessages.CANNOT_FIND_SHEET);
            }
            var spreadSheetData = await GetSpreadSheetData(sheetId);

            var indexCurrentDate = GetIndexDateColumn(spreadSheetData, DateTime.Now);
            if (indexCurrentDate == 0)
            {
                throw new OrderServiceException(ErrorMessages.CANNOT_FIND_SHEET_TODAY);
            }

            try
            {
                using HttpClient httpClient = new();
                byte[] baseImageBytes = await httpClient.GetByteArrayAsync(BASE_IMAGE_URL);
                using Image<Rgba32> baseImage = Image.Load<Rgba32>(baseImageBytes);

                // Draw date time
                baseImage.Mutate(ctx => ctx.DrawText(
                                            text: $"{DateTime.Now:dd/MM/yyyy}",
                                            font: new Font(SystemFonts.Get("Arial"), FONT_SIZE, FontStyle.Bold),
                                            color: new Color(Rgba32.ParseHex("#000000")),
                                            location: new PointF(895, 317)));

                return await ProcessCreateImage(spreadSheetData, indexCurrentDate, baseImage);

            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<Dictionary<string, string>> GetMenu(DateTime dateTime)
        {
            var sheetId = await FindSheetId(DateTime.Now);
            if (string.IsNullOrEmpty(sheetId))
            {
                throw new OrderServiceException(ErrorMessages.CANNOT_FIND_SHEET);
            }
            var spreadSheetData = await GetSpreadSheetData(sheetId);

            string dateTimeToString = dateTime.ToString("dd/MM");
            string dateTimeToStringWithAnother = dateTime.ToString("d/M");
            Dictionary<string, string> menu = new();
            foreach (var item in spreadSheetData)
            {
                if (item.Any() && (bool)(item?[0].Contains("Thực đơn")))
                {
                    for (int i = 1; i < item.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(item[i])
                            && (item[i].Contains(dateTimeToString)
                            || item[i].Contains(dateTimeToStringWithAnother)))
                        {
                            if (dateTime.DayOfWeek != DayOfWeek.Friday)
                            {
                                menu.Add(item[i + 1].Split(".")[1].Trim(), string.Empty);
                                menu.Add(item[i + 2].Split(".")[1].Trim(), string.Empty);
                                menu.Add(item[i + 3].Split(".")[1].Trim(), string.Empty);
                                menu.Add(item[i + 4].Split(".")[1].Trim(), string.Empty);
                            }
                            else
                            {
                                menu.Add(item[i + 1], string.Empty);
                            }
                            break;
                        }
                    }
                    break;
                }
            }
            return menu;
        }

        private DateTime GetLastDayOfMonth(DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, DateTime.DaysInMonth(dateTime.Year, dateTime.Month));
        }

        public async Task<bool> Order(string userName, DateTime dateTime, bool isOrder = true, bool isAll = false)
        {
            var user = _googleSheetContext.Users.Where(x => x.UserName == userName).FirstOrDefault();
            if (user is null)
            {
                throw new OrderServiceException(ErrorMessages.USER_DOES_NOT_EXIST);
            }

            var sheetId = await FindSheetId(DateTime.Now);
            if (string.IsNullOrEmpty(sheetId))
            {
                throw new OrderServiceException(ErrorMessages.CANNOT_FIND_SHEET);
            }
            var spreadSheetData = await GetSpreadSheetData(sheetId);
            var indexColumnStart = GetIndexDateColumn(spreadSheetData, dateTime) + 1;
            var indexColumnEnd = indexColumnStart + 1;

            if (isAll)
            {
                indexColumnEnd = GetIndexDateColumn(spreadSheetData, GetLastDayOfMonth(dateTime)) + 2;
            }

            int userRow = 0;
            for (int i = 0; i < spreadSheetData[0].Count; i++)
            {
                if (spreadSheetData[0][i].Contains(user.FullName.Trim()))
                {
                    userRow = i;
                    break;
                }
            }

            if (userRow == 0)
            {
                throw new OrderServiceException(ErrorMessages.USER_DOES_NOT_EXIST);
            }

            return await WriteSpreadSheet(dateTime, indexColumnStart, indexColumnEnd, userRow, sheetId, isOrder ? "x" : string.Empty);
        }

        public async Task<Dictionary<int, string>> UnPaidList()
        {
            var prevSheetId = await FindSheetId(DateTime.Now.AddMonths(-1));
            if (string.IsNullOrEmpty(prevSheetId))
            {
                throw new OrderServiceException(ErrorMessages.CANNOT_FIND_PREV_SHEET);
            }

            var prevSpreadSheetData = await GetSpreadSheetData(prevSheetId);

            Dictionary<int, string> debtMap = new();

            // Get index of the debt list
            for (int i = 0; i < prevSpreadSheetData[1].Count; i++)
            {
                (bool isUnpaid, string name) = IsUnPaid(prevSpreadSheetData, i);
                if (isUnpaid)
                {
                    debtMap.Add(i, name);
                }
            }

            return debtMap;

            static (bool, string) IsUnPaid(List<List<string>> datas, int index)
            {
                return new(!datas[1][index].Equals("v", StringComparison.OrdinalIgnoreCase) && !datas[2][index].Equals("0"), datas[0][index]);
            }
        }
    }
}
