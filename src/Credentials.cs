using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BlackScreenGuard
{
    internal sealed class Credentials
    {
        internal const int Iterations = 210000;
        private readonly byte[] salt, hash;
        private Credentials(byte[] salt, byte[] hash) { this.salt = salt; this.hash = hash; }
        internal static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackScreenGuard", "password.dat"); }
        }
        internal static string Validate(string password, string confirm)
        {
            if (password == null || password.Length < 6) return "密码至少需要 6 个字符。";
            if (password.Length > 128) return "密码最多支持 128 个字符。";
            if (String.IsNullOrWhiteSpace(password)) return "密码不能全部为空格。";
            foreach (char c in password)
                if (c < 32 || c > 126) return "请使用英文字母、数字或英文符号设置密码。";
            if (password != confirm) return "两次输入的密码不一致。";
            return null;
        }
        internal static Credentials Create(string password)
        {
            byte[] salt = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
            return new Credentials(salt, Derive(password, salt));
        }
        private static byte[] Derive(string password, byte[] salt)
        {
            using (var derive = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
                return derive.GetBytes(32);
        }
        internal bool Verify(string password)
        {
            byte[] actual = Derive(password, salt);
            int difference = 0;
            for (int i = 0; i < hash.Length; i++) difference |= hash[i] ^ actual[i];
            Array.Clear(actual, 0, actual.Length);
            return difference == 0;
        }
        internal void Save(string path)
        {
            byte[] plain = new byte[65];
            plain[0] = 1;
            Buffer.BlockCopy(salt, 0, plain, 1, 32);
            Buffer.BlockCopy(hash, 0, plain, 33, 32);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Array.Clear(plain, 0, plain.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(pending, protectedBytes);
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        internal static Credentials Load(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                if (plain.Length != 65 || plain[0] != 1) throw new InvalidDataException("密码文件格式无效。");
                byte[] salt = new byte[32], hash = new byte[32];
                Buffer.BlockCopy(plain, 1, salt, 0, 32);
                Buffer.BlockCopy(plain, 33, hash, 0, 32);
                return new Credentials(salt, hash);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
    }
}
