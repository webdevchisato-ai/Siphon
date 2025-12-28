using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Siphon.Services
{
    public class UserConfig
    {
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string Salt { get; set; }

        // New: How long to keep pending files (in minutes). Default 2880 (2 days)
        public int PendingPreservationMinutes { get; set; } = 2880;
    }

    public class UserService
    {
        private readonly string _configPath;

        public UserService()
        {
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config", "userConfig.json");
        }

        public bool IsSetupRequired() => !File.Exists(_configPath);

        public int GetPreservationMinutes()
        {
            try
            {
                if (!File.Exists(_configPath)) return 2880; // Default 2 Days
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configPath));
                return config?.PendingPreservationMinutes ?? 2880;
            }
            catch { return 2880; }
        }

        public void CreateUser(string username, string password, int preservationMinutes)
        {
            byte[] salt = new byte[128 / 8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            string hashed = HashPassword(password, salt);

            var config = new UserConfig
            {
                Username = username,
                PasswordHash = hashed,
                Salt = Convert.ToBase64String(salt),
                PendingPreservationMinutes = preservationMinutes
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }

        public bool ValidateUser(string username, string password)
        {
            if (!File.Exists(_configPath)) return false;

            try
            {
                string json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<UserConfig>(json);

                if (config == null || !string.Equals(config.Username, username, StringComparison.OrdinalIgnoreCase))
                    return false;

                byte[] salt = Convert.FromBase64String(config.Salt);
                string hashedInput = HashPassword(password, salt);

                return hashedInput == config.PasswordHash;
            }
            catch
            {
                return false;
            }
        }

        private string HashPassword(string password, byte[] salt)
        {
            return Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));
        }
    }
}