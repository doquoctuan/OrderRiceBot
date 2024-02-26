﻿using OrderRice.Entities;
using OrderRice.Interfaces;
using OrderRice.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrderRice.Services
{
    public class UserService : IUserService
    {
        private readonly OrderLunchDbContext _dbContext;

        public UserService(OrderLunchDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<Users> CreateUser(Users user)
        {
            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();
            return user;
        }

        public bool DeleteUser(string userName)
        {
            throw new NotImplementedException();
        }

        public Users FindByUserName()
        {
            throw new NotImplementedException();
        }

        public List<Users> GetList()
        {
            throw new NotImplementedException();
        }

        public Users UpdateUser(Users user)
        {
            throw new NotImplementedException();
        }
    }
}
