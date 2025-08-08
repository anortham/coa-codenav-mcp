using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestFixtures
{
    /// <summary>
    /// A sample service class for testing navigation and analysis tools
    /// </summary>
    public class SampleService : IDisposable
    {
        private readonly string _connectionString;
        private bool _disposed;

        public SampleService(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Gets user data by ID
        /// </summary>
        /// <param name="userId">The user identifier</param>
        /// <returns>User data or null if not found</returns>
        public async Task<UserData?> GetUserAsync(int userId)
        {
            if (userId <= 0)
                throw new ArgumentException("User ID must be positive", nameof(userId));

            // Simulate async operation
            await Task.Delay(100);
            
            return new UserData 
            { 
                Id = userId, 
                Name = $"User {userId}",
                Email = $"user{userId}@example.com"
            };
        }

        /// <summary>
        /// Gets all users
        /// </summary>
        public List<UserData> GetAllUsers()
        {
            var users = new List<UserData>();
            for (int i = 1; i <= 10; i++)
            {
                users.Add(new UserData 
                { 
                    Id = i, 
                    Name = $"User {i}",
                    Email = $"user{i}@example.com"
                });
            }
            return users;
        }

        /// <summary>
        /// Method with complex cyclomatic complexity for code metrics testing
        /// </summary>
        public string ProcessUserData(UserData user, bool validateEmail, bool checkPermissions)
        {
            if (user == null)
                return "Invalid user";

            if (string.IsNullOrEmpty(user.Name))
                return "Name required";

            if (validateEmail)
            {
                if (string.IsNullOrEmpty(user.Email))
                    return "Email required";
                
                if (!user.Email.Contains("@"))
                    return "Invalid email format";
            }

            if (checkPermissions)
            {
                if (user.Id < 100)
                    return "Insufficient permissions";
                
                if (user.Name.StartsWith("Admin"))
                    return "Admin access granted";
            }

            return "User processed successfully";
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Dispose managed resources
                _disposed = true;
            }
        }
    }

    public class UserData
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}