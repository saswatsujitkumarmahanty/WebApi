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

            // --- NEW: Phone Number Normalization ---
            string cleanPhone = loginDto.Phone;

            if (!string.IsNullOrEmpty(cleanPhone))
            {
                // Remove any '+' symbols
                cleanPhone = cleanPhone.Replace("+", "").Trim();

                // If it includes the country code (91) and is exactly 12 digits long, keep only the last 10 digits
                if (cleanPhone.StartsWith("91") && cleanPhone.Length == 12)
                {
                    cleanPhone = cleanPhone.Substring(2);
                }
            }
            // ---------------------------------------

            // 1. Verify user credentials exist in SQL Server first using the CLEANED phone number
            var user = dbContext.Users.FirstOrDefault(u =>
                u.Email == loginDto.Email &&
                u.Phone == cleanPhone);

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

            // NEW: Re-attach the '91' country code so MSG91 can route the text message correctly
            var smsTask = SendMockSmsAsync("91" + cleanPhone, otpCode);

            await Task.WhenAll(emailTask, smsTask);

            // Tell Angular to flip over to the verification form entry step
            return Ok(new
            {
                message = "OTP sent successfully to your email and phone number.",
                requiresOtp = true,
                email = user.Email
            });
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

        /* ====================================================================
           INFRASTRUCTURE GATEWAYS PLACEHOLDERS
           Replace the internal Console printing with your actual gateway SDKs
           (e.g., Twilio for SMS, MailKit / SendGrid for Email messages)
           ==================================================================== */

        private async Task SendMockEmailAsync(string targetEmail, string code)
        {

            var smtpServer = _configuration["SmtpSettings:Server"];
            var smtpPort = int.Parse(_configuration["SmtpSettings:Port"] ?? "587");
            var senderName = _configuration["SmtpSettings:SenderName"];
            var senderEmail = _configuration["SmtpSettings:SenderEmail"];
            var appPassword = _configuration["SmtpSettings:AppPassword"];

            // 1. Construct the email message
            var message = new MimeMessage();

            // TODO: Replace with your actual sender name and email
            message.From.Add(new MailboxAddress("Registration App", "YOUR_EMAIL@gmail.com"));
            message.To.Add(new MailboxAddress("", targetEmail));
            message.Subject = "Your Login Verification Code";

            // 2. Set the email body
            message.Body = new TextPart("plain")
            {
                Text = $"Hello!\n\nYour secure login validation token code is: {code}\n\nThis code will expire in 5 minutes."
            };

            // 3. Connect to the SMTP server and send
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
                    // Log the error in your console so you can troubleshoot if it fails
                    Console.WriteLine($"\n[SMTP ERROR] Failed to send email to {targetEmail}: {ex.Message}\n");
                }
                finally
                {
                    // Always disconnect cleanly
                    await client.DisconnectAsync(true);
                }
            }
        }
        private async Task SendMockSmsAsync(string targetPhone, string code)
        {
            var authKey = _configuration["Msg91Settings:AuthKey"];

            // --- STEP 3: Read the Template ID from appsettings.json ---
            var templateId = _configuration["Msg91Settings:TemplateId"];

            using (var client = new HttpClient())
            {
                try
                {
                    // Clean up mobile numbers to remove any leading '+' symbols if present
                    targetPhone = targetPhone.Replace("+", "").Trim();

                    // --- STEP 3: Inject template_id into the query string ---
                    string otpApiUrl = $"https://control.msg91.com/api/v5/otp?template_id={templateId}&mobile={targetPhone}&otp={code}&authkey={authKey}";

                    var response = await client.PostAsync(otpApiUrl, null);

                    if (response.IsSuccessStatusCode)
                    {
                        string contents = await response.Content.ReadAsStringAsync();

                        // This will now print the success JSON instead of failing silently!
                        Console.WriteLine($"\n[MSG91 RESPONSE] {contents}\n");
                    }
                    else
                    {
                        Console.WriteLine($"\n[OTP ROUTE ERROR] Gateway response status: {response.StatusCode}\n");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[CRITICAL FAULT] Failed contacting OTP network infrastructure: {ex.Message}\n");
                }
            }
        }
    
    }
}
