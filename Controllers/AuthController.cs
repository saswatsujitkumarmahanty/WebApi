using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Registration.Model.Entity;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Registration.Data
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly RegistrationDbContext dbContext;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;
        private const string CacheKeyPrefix = "OTP_";

        public AuthController(RegistrationDbContext dbContext, IMemoryCache cache, IConfiguration configuration)
        {
            this.dbContext = dbContext;
            this._cache = cache;
            this._configuration = configuration;
        }

        // POST: api/Auth/signup
        [HttpPost("signup")]
        public IActionResult SignUp([FromBody] User newUser)
        {
            if (newUser == null)
                return BadRequest(new { message = "Invalid user data." });

            if (dbContext.Users.Any(u => u.Email == newUser.Email))
            {
                return BadRequest(new { message = "Email is already registered." });
            }

            newUser.Id = Guid.NewGuid();
            dbContext.Users.Add(newUser);
            dbContext.SaveChanges();

            return Ok(new { message = "Registration successful!" });
        }

        // POST: api/Auth/login (STEP 1: Validate User & Dispatch OTP)
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            if (loginDto == null)
                return BadRequest(new { message = "Please provide login details." });

            // 1. Verify user credentials exist in SQL Server first (Original exact match)
            var user = dbContext.Users.FirstOrDefault(u =>
                u.Email == loginDto.Email &&
                u.Phone == loginDto.Phone);

            if (user == null)
            {
                return Unauthorized(new { message = "Invalid email or phone number." });
            }

            // 2. Generate a secure 6-digit OTP
            string otpCode = Random.Shared.Next(100000, 999999).ToString();

            // 3. Cache the OTP tied to the user email for exactly 5 minutes
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));

            _cache.Set(CacheKeyPrefix + loginDto.Email, otpCode, cacheOptions);

            // 4. Fire dispatch methods simultaneously in parallel background threads
            var emailTask = SendMockEmailAsync(user.Email, otpCode);
            var smsTask = SendMockSmsAsync(user.Phone, otpCode);

            await Task.WhenAll(emailTask, smsTask);

            // Tell Angular to flip over to the verification form entry step
            return Ok(new
            {
                message = "OTP sent successfully to your email and phone number.",
                requiresOtp = true,
                email = user.Email
            });
        }

        // PUT: api/Auth/update-name/{id}
        [HttpPut("update-name/{id}")]
        public IActionResult UpdateName(Guid id, [FromBody] UpdateNameDto dto)
        {
            var user = dbContext.Users.FirstOrDefault(u => u.Id == id);
            if (user == null)
            {
                return NotFound(new { message = "User not found." });
            }

            user.Name = dto.Name;
            dbContext.SaveChanges();

            return Ok(new { message = "Name updated successfully!", newName = user.Name });
        }

        public class UpdateNameDto
        {
            public string Name { get; set; } = string.Empty;
        }

        // POST: api/Auth/verify-otp (STEP 2: Validate OTP Code & Complete Login)
        [HttpPost("verify-otp")]
        public IActionResult VerifyOtp([FromBody] OtpVerificationDto verificationDto)
        {
            if (verificationDto == null)
                return BadRequest(new { message = "Invalid entry attempt." });

            string cacheKey = CacheKeyPrefix + verificationDto.Email;

            // 1. Check if an OTP code exists in memory for this specific email
            if (_cache.TryGetValue(cacheKey, out string? cachedCode))
            {
                // 2. Verify match
                if (cachedCode == verificationDto.OtpCode)
                {
                    // Wipe the cache entry instantly to protect against code re-use attacks
                    _cache.Remove(cacheKey);

                    // Fetch the user information profile to return authentication context
                    var user = dbContext.Users.FirstOrDefault(u => u.Email == verificationDto.Email);

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
            // 3. Read the values dynamically from appsettings.json
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
                    // 4. Use the dynamic variables instead of hardcoded strings
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
            await Task.Delay(50); // Simulate asynchronous network I/O latency
            Console.WriteLine($"\n[GATEWAY OUTBOUND SMS] To: {targetPhone} | Msg: Use code {code} to complete login authorization.\n");
        }
    }

}