﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Server.ViewModels;
using Server.Models;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Server.Controllers
{
    [Route("account")]
    public class AccountController : Controller
    {
        readonly DatabaseConfiguration databaseConfiguration;

        public AccountController(IOptions<DatabaseConfiguration> _databaseConfiguration) {
            databaseConfiguration = _databaseConfiguration.Value;
        }

        [HttpGet("login")]
        public IActionResult Login(string returnUrl)
        {
            return View(new LoginViewModel());
        }

        [HttpGet("logout")]
        public async Task<IActionResult> Logout() {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(string returnUrl, LoginSubmission submission) {
            using(var conn = new NpgsqlConnection(databaseConfiguration.ConnectionString))
            using(var cmd = new NpgsqlCommand()) {
                await conn.OpenAsync();
                cmd.Connection = conn;
                cmd.CommandText = "SELECT id,displayName,password_digest FROM account WHERE email=@email;";
                cmd.Parameters.AddWithValue("@email", submission.Email ?? string.Empty);
                using(var reader = await cmd.ExecuteReaderAsync()) {
                    Account account = null;
                    if(await reader.ReadAsync()) {
                        var guid = reader.GetGuid(0);
                        var displayName = reader.GetString(1);
                        var extantDigest = new byte[384 / 8];
                        reader.GetBytes(2, 0, extantDigest, 0, extantDigest.Length);
                        var isValid = EvaluatePassword(submission.Password, extantDigest);
                        if(isValid) {
                            account = new Account(guid, displayName);
                        }
                    }
                    if(account != null) {
                        var claims = new[] {
                            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(), ClaimValueTypes.String),
                            new Claim(ClaimTypes.Name, account.DisplayName, ClaimValueTypes.String)
                        };
                        var claimsIdentity = new ClaimsIdentity(claims, "SecureLogin");
                        var authProperties = new AuthenticationProperties
                        {
                            ExpiresUtc = DateTimeOffset.UtcNow.AddYears(1),
                            IsPersistent = true
                        };
                        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                                                      new ClaimsPrincipal(claimsIdentity),
                                                      authProperties);
                        return Redirect(returnUrl ?? "/");
                    }
                    return View(new LoginViewModel(true));
                }
            }
        }

        [HttpGet("register")]
        public IActionResult Register()
        {
            return View(new RegistrationViewModel());
        }

        static bool EvaluatePassword(string password, byte[] extantDigest) {
            var extantSalt = new byte[128 / 8];
            var extantHash = new byte[256 / 8];

            Array.Copy(extantDigest, extantSalt, extantSalt.Length);
            Array.Copy(extantDigest, extantSalt.Length, extantHash, 0, extantHash.Length);

            var hash = KeyDerivation.Pbkdf2(
                password: password,
                salt: extantSalt,
                prf: KeyDerivationPrf.HMACSHA1,
                iterationCount: 10000,
                numBytesRequested: 256 / 8);

            var match = true;

            System.Diagnostics.Debug.Assert(hash.Length == extantHash.Length);

            for (var i = 0; i < extantHash.Length; i++) {
                match &= extantHash[i] == hash[i];
            }

            return match;
        }

        static byte[] GetPasswordDigest(string password) {
            // generate a 128-bit salt using a secure PRNG
            var salt = new byte[128 / 8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            // derive a 256-bit subkey (use HMACSHA1 with 10,000 iterations)
            var hashed = KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA1,
                iterationCount: 10000,
                numBytesRequested: 256 / 8);

            var digest = new byte[384 / 8];

            Array.Copy(salt, digest, salt.Length);
            Array.Copy(hashed, 0, digest, salt.Length, hashed.Length);

            return digest;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(RegistrationSubmission submission)
        {
            var failureBuilder = ImmutableArray.CreateBuilder<RegistrationFailureReasons>();
            if(VetEmail(submission.Email) == false) {
                failureBuilder.Add(RegistrationFailureReasons.EmailIsInvalid);
            }
            if(VetPassword(submission.Password) == false) {
                failureBuilder.Add(RegistrationFailureReasons.PasswordIsInadequate);
            }
            if(VetDisplayName(submission.DisplayName) == false) {
                failureBuilder.Add(RegistrationFailureReasons.DisplayNameIsBlank);
            }
            if(failureBuilder.Any() == false) {
                using (var conn = new NpgsqlConnection(databaseConfiguration.ConnectionString))
                using (var cmd = new NpgsqlCommand())
                {
                    await conn.OpenAsync();
                    cmd.Connection = conn;
                    cmd.CommandText = "INSERT INTO account(id,displayName,email,password_digest) values(@guid,@displayName,@email,@password_digest)";
                    cmd.Parameters.AddWithValue("@guid", Guid.NewGuid());
                    cmd.Parameters.AddWithValue("@displayName", submission.DisplayName ?? string.Empty);
                    cmd.Parameters.AddWithValue("@email", submission.Email ?? string.Empty);
                    cmd.Parameters.AddWithValue("@password_digest", GetPasswordDigest(submission.Password ?? string.Empty));
                    try {
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch(PostgresException ex) {
                        if(ex.SqlState == "23505") {
                            failureBuilder.Add(RegistrationFailureReasons.EmailIsInUse);
                        }
                        else {
                            throw ex;
                        }
                    }
                }
            }
            if(failureBuilder.Any()) {
                return View(new RegistrationViewModel(failureBuilder));
            }
            return RedirectToAction(nameof(Login));
        }

        static bool VetEmail(string email)
        {
            return EmailValidation.EmailValidator.Validate(email);
        }

        static bool VetDisplayName(string displayName)
        {
            return string.IsNullOrWhiteSpace(displayName) == false;
        }

        static bool VetPassword(string password)
        {
            return password.Length >= 6;
        }
    }
}