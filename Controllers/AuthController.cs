using Application.Dto;
using Application.Interfaces;
using Domain.Entity;
using Infrastructure.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using MimeKit;
using System.Security.Claims;

namespace WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;
        private readonly ITokenService _tokenService;
        private const string CacheKeyPrefix = "OTP_";
        private static readonly string[] ValidRoles = { "User", "Admin" };

        public AuthController(IUserRepository userRepository, IMemoryCache cache, IConfiguration configuration, ITokenService tokenService)
        {
            _userRepository = userRepository;
            _cache = cache;
            _configuration = configuration;
            _tokenService = tokenService;
        }

        // POST: api/Auth/signup
        [HttpPost("signup")]
        public async Task<IActionResult> SignUp([FromBody] RegisterDto registerDto)
        {
            if (registerDto == null)
                return BadRequest(new { message = "Invalid user data." });

            var newUser = new User
            {
                Name = registerDto.Name,
                Gender = registerDto.Gender,
                Email = registerDto.Email,
                Phone = registerDto.Phone,
                Age = registerDto.Age,
                Role = "User" // NEW — every signup defaults to User; Admins are promoted manually/by another Admin
            };

            bool emailExists = await _userRepository.UserExistsByEmailAsync(newUser.Email);
            if (emailExists)
            {
                return BadRequest(new { message = "Email is already registered." });
            }

            await _userRepository.AddUserAsync(newUser);

            return Ok(new { message = "Registration successful!" });
        }

        [HttpPost("login-password")]
        public async Task<IActionResult> LoginWithPassword([FromBody] LoginPasswordDto loginDto)
        {
            if (loginDto == null)
                return BadRequest(new { message = "Please provide login details." });

            // Uses the method that only checks the email
            var user = await _userRepository.GetUserByEmailAsync(loginDto.Email);

            if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !BCrypt.Net.BCrypt.Verify(loginDto.Password, user.PasswordHash))
            {
                return Unauthorized(new { message = "Invalid email or password." });
            }

            // Direct login success! No OTP required.
            var token = _tokenService.GenerateToken(user.Id, user.Email, user.Name, user.Role); // CHANGED — now includes role

            return Ok(new
            {
                message = "Login successful!",
                userId = user.Id,
                name = user.Name,
                role = user.Role, // NEW
                token
            });
        }

        [Authorize]
        [HttpPost("set-password")]
        public async Task<IActionResult> SetPassword([FromBody] LoginPasswordDto passwordDto)
        {
            if (passwordDto == null || string.IsNullOrEmpty(passwordDto.Password))
            {
                return BadRequest(new { message = "Please provide a valid password." });
            }

            var callerEmail = User.FindFirst(JwtRegisteredClaimNames.Email)?.Value;
            if (string.IsNullOrEmpty(callerEmail))
            {
                return Unauthorized();
            }

            var user = await _userRepository.GetUserByEmailAsync(callerEmail);
            if (user == null)
            {
                return NotFound(new { message = "User not found." });
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(passwordDto.Password);
            await _userRepository.UpdateUserAsync(user);

            return Ok(new { message = "Password set successfully!", name = user.Name });
        }

        // POST: api/Auth/login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            if (loginDto == null)
                return BadRequest(new { message = "Please provide login details." });

            var user = await _userRepository.GetUserByEmailAsync(loginDto.Email);

            if (user == null || user.Phone != loginDto.Phone)
            {
                return Unauthorized(new { message = "Invalid email or phone number." });
            }

            string otpCode = Random.Shared.Next(100000, 999999).ToString();

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));

            _cache.Set(CacheKeyPrefix + loginDto.Email, otpCode, cacheOptions);

            var emailTask = SendMockEmailAsync(user.Email, otpCode);
            var smsTask = SendMockSmsAsync(user.Phone, otpCode);

            await Task.WhenAll(emailTask, smsTask);

            return Ok(new
            {
                message = "OTP sent successfully to your email and phone number.",
                requiresOtp = true,
                email = user.Email
            });
        }

        // PUT: api/Auth/update-name/{id}
        [Authorize]
        [HttpPut("update-name/{id}")]
        public async Task<IActionResult> UpdateName(Guid id, [FromBody] UpdateEmployeeDto dto)
        {
            var callerIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (callerIdClaim == null || !Guid.TryParse(callerIdClaim, out var callerId) || callerId != id)
            {
                return Forbid();
            }

            bool updated = await _userRepository.UpdateUserNameAsync(id, dto.Name);
            if (!updated)
            {
                return NotFound(new { message = "User not found." });
            }

            return Ok(new { message = "Name updated successfully!", newName = dto.Name });
        }

        // GET: api/Auth/me — NEW
        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            var idClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var email = User.FindFirst(JwtRegisteredClaimNames.Email)?.Value;
            var name = User.FindFirst(ClaimTypes.Name)?.Value;
            var role = User.FindFirst(ClaimTypes.Role)?.Value;

            if (idClaim == null || !Guid.TryParse(idClaim, out var userId))
            {
                return Unauthorized();
            }

            return Ok(new
            {
                userId,
                email,
                name,
                role
            });
        }

        // PUT: api/Auth/set-role/{id} — NEW, Admin-only
        [Authorize(Roles = "Admin")]
        [HttpPut("set-role/{id:guid}")]
        public async Task<IActionResult> SetRole(Guid id, [FromBody] SetRoleDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Role))
            {
                return BadRequest(new { message = "Please provide a valid role." });
            }

            if (!ValidRoles.Contains(dto.Role, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = $"Role must be one of: {string.Join(", ", ValidRoles)}" });
            }

            bool updated = await _userRepository.UpdateUserRoleAsync(id, dto.Role);
            if (!updated)
            {
                return NotFound(new { message = "User not found." });
            }

            return Ok(new { message = "Role updated successfully!", userId = id, role = dto.Role });
        }

        // POST: api/Auth/verify-otp
        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] OtpVerificationDto verificationDto)
        {
            if (verificationDto == null)
                return BadRequest(new { message = "Invalid entry attempt." });

            string cacheKey = CacheKeyPrefix + verificationDto.Email;

            if (_cache.TryGetValue(cacheKey, out string? cachedCode))
            {
                if (cachedCode == verificationDto.OtpCode)
                {
                    _cache.Remove(cacheKey);

                    var user = await _userRepository.GetUserByEmailAsync(verificationDto.Email);

                    if (user == null)
                        return NotFound(new { message = "User profile record no longer exists." });

                    var token = _tokenService.GenerateToken(user.Id, user.Email, user.Name, user.Role); // CHANGED

                    return Ok(new
                    {
                        message = "Login successful!",
                        userId = user.Id,
                        name = user.Name,
                        role = user.Role, // NEW
                        token
                    });
                }
            }

            return Unauthorized(new { message = "The code entered is incorrect or has expired. Please try again." });
        }

        private async Task SendMockEmailAsync(string targetEmail, string code)
        {
            var smtpServer = _configuration["SmtpSettings:Server"];
            var smtpPort = int.Parse(_configuration["SmtpSettings:Port"] ?? "587");
            var senderName = _configuration["SmtpSettings:SenderName"];
            var senderEmail = _configuration["SmtpSettings:SenderEmail"];
            var appPassword = _configuration["SmtpSettings:AppPassword"];

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(senderName, senderEmail));
            message.To.Add(new MailboxAddress("", targetEmail));
            message.Subject = "Your Login Verification Code";

            message.Body = new TextPart("plain")
            {
                Text = $"Hello!\n\nYour secure login validation token code is: {code}\n\nThis code will expire in 5 minutes."
            };

            using (var client = new SmtpClient())
            {
                try
                {
                    await client.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls);
                    client.AuthenticationMechanisms.Remove("XOAUTH2");
                    await client.AuthenticateAsync(senderEmail, appPassword);
                    await client.SendAsync(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[SMTP ERROR] Failed to send email to {targetEmail}: {ex.Message}\n");
                }
                finally
                {
                    await client.DisconnectAsync(true);
                }
            }
        }

        private async Task SendMockSmsAsync(string targetPhone, string code)
        {
            await Task.Delay(50);
            Console.WriteLine($"\n[GATEWAY OUTBOUND SMS] To: {targetPhone} | Msg: Use code {code} to complete login authorization.\n");
        }
    }
}