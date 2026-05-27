// Test fixture: Code that should trigger MOD008 (file-scoped namespace)
using System;
using System.Collections.Generic;

namespace TestApp.Services
{
    public class UserService
    {
        private readonly List<string> _users = new List<string>();

        public void AddUser(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            _users.Add(name);
        }

        public IReadOnlyList<string> GetUsers()
        {
            return _users;
        }
    }
}
