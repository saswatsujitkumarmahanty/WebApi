using Domain.Entity;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Data
{
    public class RegistrationDbContext : DbContext
    {
        public RegistrationDbContext(DbContextOptions<RegistrationDbContext> options) : base(options) 
        { 
        
        
        }
        public DbSet<Employee> Employees { get; set; }

        public DbSet<User> Users { get; set; }

    }
}
