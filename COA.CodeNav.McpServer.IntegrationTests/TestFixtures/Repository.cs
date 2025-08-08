using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TestFixtures
{
    public interface IRepository<T> where T : class
    {
        Task<T?> GetByIdAsync(int id);
        Task<IEnumerable<T>> GetAllAsync();
        Task<T> CreateAsync(T entity);
        Task UpdateAsync(T entity);
        Task DeleteAsync(int id);
    }

    /// <summary>
    /// Generic repository implementation for testing inheritance and generics
    /// </summary>
    public class Repository<T> : IRepository<T> where T : class, IEntity
    {
        private readonly List<T> _entities = new();
        private int _nextId = 1;

        public virtual async Task<T?> GetByIdAsync(int id)
        {
            await Task.Delay(10); // Simulate async
            return _entities.FirstOrDefault(e => e.Id == id);
        }

        public virtual async Task<IEnumerable<T>> GetAllAsync()
        {
            await Task.Delay(10); // Simulate async
            return _entities.ToList();
        }

        public virtual async Task<T> CreateAsync(T entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            entity.Id = _nextId++;
            _entities.Add(entity);
            
            await Task.Delay(10); // Simulate async
            return entity;
        }

        public virtual async Task UpdateAsync(T entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            var existing = await GetByIdAsync(entity.Id);
            if (existing == null)
                throw new InvalidOperationException($"Entity with ID {entity.Id} not found");

            var index = _entities.IndexOf(existing);
            _entities[index] = entity;
            
            await Task.Delay(10); // Simulate async
        }

        public virtual async Task DeleteAsync(int id)
        {
            var entity = await GetByIdAsync(id);
            if (entity != null)
            {
                _entities.Remove(entity);
            }
            
            await Task.Delay(10); // Simulate async
        }
    }

    public interface IEntity
    {
        int Id { get; set; }
    }

    public class User : IEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Specialized repository that overrides base behavior
    /// </summary>
    public class UserRepository : Repository<User>
    {
        public override async Task<User> CreateAsync(User entity)
        {
            if (string.IsNullOrWhiteSpace(entity.Email))
                throw new ArgumentException("Email is required", nameof(entity));

            if (!entity.Email.Contains("@"))
                throw new ArgumentException("Invalid email format", nameof(entity));

            return await base.CreateAsync(entity);
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            var users = await GetAllAsync();
            return users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }
    }
}