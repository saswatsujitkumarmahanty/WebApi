using Application.Dto;
using Application.Interfaces;
using Domain.Entity;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;

namespace WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;
        private const string CacheKeyPrefix = "OTP_";

        // Inject IUserRepository instead of RegistrationDbContext
        public AuthController(IUserRepository userRepository, IMemoryCache cache, IConfiguration configuration)
        {
            _userRepository = userRepository;
            _cache = cache;
            _configuration = configuration;
        }

        // POST: api/Auth/signup
        [HttpPost("signup")]
        public async Task<IActionResult> SignUp([FromBody] User newUser)
        {
            if (newUser == null)
                return BadRequest(new { message = "Invalid user data." });

            bool emailExists = await _userRepository.UserExistsByEmailAsync(newUser.Email);
            if (emailExists)
            {
                return BadRequest(new { message = "Email is already registered." });
            }

            await _userRepository.AddUserAsync(newUser);

            return Ok(new { message = "Registration successful!" });
        }

        // POST: api/Auth/login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            if (loginDto == null)
                return BadRequest(new { message = "Please provide login details." });

            var user = await _userRepository.GetUserByEmailAndPhoneAsync(loginDto.Email, loginDto.Phone);

            if (user == null)
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
        [HttpPut("update-name/{id}")]
        public async Task<IActionResult> UpdateName(Guid id, [FromBody] UpdateNameDto dto)
        {
            bool updated = await _userRepository.UpdateUserNameAsync(id, dto.Name);
            if (!updated)
            {
                return NotFound(new { message = "User not found." });
            }

            return Ok(new { message = "Name updated successfully!", newName = dto.Name });
        }

        public class UpdateNameDto
        {
            public string Name { get; set; } = string.Empty;
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

                    return Ok(new
                    {
                        message = "Login successful!",
                        userId = user.Id,
                        name = user.Name
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