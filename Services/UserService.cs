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
        public int PendingPreservationMinutes { get; set; } = 2880;
        public int ApporvedRetentionMins { get; set; } = 60;
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
                if (!File.Exists(_configPath)) return 2880;
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configPath));
                return config?.PendingPreservationMinutes ?? 2880;
            }
            catch { return 2880; }
        }

        public int GetApprovedRetentionMinutes()
        {
            try
            {
                if (!File.Exists(_configPath)) return 60;
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configPath));
                return config?.ApporvedRetentionMins ?? 60;
            }
            catch { return 60; }
        }

        public UserConfig GetUserConfig()
        {
            if (!File.Exists(_configPath)) return null;
            try { return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configPath)); }
            catch { return null; }
        }

        public void CreateUser(string username, string password, int preservationMinutes, int approvedPresservationMinutes)
        {
            // Initial creation
            var config = new UserConfig
            {
                Username = username,
                PendingPreservationMinutes = preservationMinutes,
                ApporvedRetentionMins = approvedPresservationMinutes
            };
            SetPassword(config, password);
            SaveConfig(config);
        }

        public void UpdateConfiguration(string username, string newPassword, int preservationMinutes, int ApporvedRetentionMins)
        {
            // Load existing to preserve Salt/Hash if password isn't changing
            var config = GetUserConfig() ?? new UserConfig();

            config.Username = username;
            config.PendingPreservationMinutes = preservationMinutes;
            config.ApporvedRetentionMins = ApporvedRetentionMins;

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                SetPassword(config, newPassword);
            }

            SaveConfig(config);
        }

        public bool ValidateUser(string username, string password)
        {
            if (!File.Exists(_configPath)) return false;
            try
            {
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configPath));
                if (config == null || !string.Equals(config.Username, username, StringComparison.OrdinalIgnoreCase))
                    return false;

                byte[] salt = Convert.FromBase64String(config.Salt);
                string hashedInput = HashPassword(password, salt);
                return hashedInput == config.PasswordHash;
            }
            catch { return false; }
        }

        private void SetPassword(UserConfig config, string password)
        {
            byte[] salt = new byte[128 / 8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            config.Salt = Convert.ToBase64String(salt);
            config.PasswordHash = HashPassword(password, salt);
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

        private void SaveConfig(UserConfig config)
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_configPath, json);
        }
    }
}