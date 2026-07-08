using Microsoft.EntityFrameworkCore;
using Registration.Domain.Entity;

namespace Registration.Data
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
