using Application.Dto;
using Application.Interfaces;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmployeeController : ControllerBase
    {
        private readonly IEmployeeRepository _repository;

        // Inject the interface, not the database logic
        public EmployeeController(IEmployeeRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllEmployees()
        {
            var employees = await _repository.GetAllEmployeesAsync();
            return Ok(employees);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetAllEmployeesById(Guid id)
        {
            var employee = await _repository.GetEmployeeByIdAsync(id);
            if (employee == null) return NotFound();

            return Ok(employee);
        }

        [HttpPost]
        public async Task<IActionResult> AddEmployee([FromBody] AddEmployeeDto addEmployeeDto)
        {
            var createdEmployee = await _repository.AddEmployeeAsync(addEmployeeDto);
            return Ok(createdEmployee);
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> UpdateEmployee(Guid id, [FromBody] UpdateEmployeeDto updateEmployeeDto)
        {
            var updatedEmployee = await _repository.UpdateEmployeeAsync(id, updateEmployeeDto);
            if (updatedEmployee == null) return NotFound();

            return Ok(updatedEmployee);
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteEmployee(Guid id)
        {
            var success = await _repository.DeleteEmployeeAsync(id);
            if (!success) return NotFound();

            return Ok(new { message = "Employee deleted successfully.", id });
        }
    }
}