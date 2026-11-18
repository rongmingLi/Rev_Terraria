using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TerrariaMod.SaveIO
{
    /// <summary>
    /// Symmetric encryption helper matching Terraria's .plr file format:
    /// AES-128-CBC (RijndaelManaged, PKCS7) using UTF-16LE bytes of "h3y_gUyZ" as both Key and IV.
    /// </summary>
    public static class SaveCrypto
    {
        private static readonly byte[] PlayerKeyAndIv = Encoding.Unicode.GetBytes("h3y_gUyZ");

        public static byte[] DecryptPlayerBytes(byte[] cipherBytes)
        {
            if (cipherBytes == null || cipherBytes.Length == 0)
                throw new ArgumentException("Cipher buffer is empty.", nameof(cipherBytes));

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var input = new MemoryStream(cipherBytes))
                using (var crypto = new CryptoStream(input, aes.CreateDecryptor(PlayerKeyAndIv, PlayerKeyAndIv), CryptoStreamMode.Read))
                using (var output = new MemoryStream())
                {
                    crypto.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        public static byte[] EncryptPlayerBytes(byte[] plainBytes)
        {
            if (plainBytes == null)
                throw new ArgumentNullException(nameof(plainBytes));

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var output = new MemoryStream())
                {
                    using (var crypto = new CryptoStream(output, aes.CreateEncryptor(PlayerKeyAndIv, PlayerKeyAndIv), CryptoStreamMode.Write))
                    {
                        crypto.Write(plainBytes, 0, plainBytes.Length);
                        crypto.FlushFinalBlock();
                    }
                    return output.ToArray();
                }
            }
        }
    }
}
