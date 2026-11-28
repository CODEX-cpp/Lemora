using System;
using Lemora.Model.Protocol.Base;
using Lemora.Utils;
using Newtonsoft.Json;
using NUlid;
using Shawn.Utils;

namespace Lemora.Service.DataSource.DAO.Dapper
{
    public class TableCredential
    {
        public const string TABLE_NAME = "Credentials";
        /// <summary>
        /// use for update, delete
        /// </summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>
        /// use for find
        /// </summary>
        public string Name { get; set; } = string.Empty;
        public string Json { get; set; } = "";
        public string Hash { get; set; } = "";


        /// <summary>
        /// Value of the Name column: HMAC hash of the name, bound to the vault key. It used to hold the plaintext name
        /// ("customer / site / DOMAIN\user"): the database file alone revealed customers, domains and accounts
        /// (security hardening). The real name stays in the encrypted Json.
        /// Always the same for the same name: the column stays UNIQUE and deletion by name works.
        /// </summary>
        public static string OpaqueName(string name)
        {
            var key = DataVault.CurrentKey();
            // shortcut: with the vault locked (does not happen when writing) the name is kept; GetCredentials rewrites it on the next load with the vault unlocked
            if (key == null || string.IsNullOrEmpty(name)) return name;
            using var hmac = new System.Security.Cryptography.HMACSHA256(key);
            return "n:" + Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("credential-name|" + name))).ToLowerInvariant();
        }

        public static TableCredential FromCredential(Credential credential)
        {
            var tableCredential = new TableCredential()
            {
                Id = Ulid.NewUlid().ToString(),
                Name = OpaqueName(credential.Name),
                Hash = credential.GetHash(),
                Json = DataVault.SimpleEncrypt(JsonConvert.SerializeObject(credential)),
            };
            if (string.IsNullOrEmpty(tableCredential.Name))
            {
                tableCredential.Name = tableCredential.Id; // fallback to Id if Name is empty
            }
            return tableCredential;
        }

        public Credential? ToCredential() => ToCredential(out _);

        /// <param name="nameFromJson">false = the Json has no name (old rows) and the column is used: that row must not be rewritten</param>
        public Credential? ToCredential(out bool nameFromJson)
        {
            nameFromJson = false;
            try
            {
                var c = JsonConvert.DeserializeObject<Credential>(DataVault.DecryptOrReturnOriginalString(Json));
                if (c != null)
                {
                    c.DatabaseId = Id;
                    nameFromJson = !string.IsNullOrEmpty(c.Name);
                    if (!nameFromJson) c.Name = Name;   // the real name is in the encrypted Json; the column is only a hash
                    c.Hash = Hash;
                }
                return c;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error($"Failed to deserialize Credential from JSON: {Json}", e);
                return null;
            }
        }
    }



    internal static class TableCredentialHelperStatic
    {
        public static TableCredential ToTableCredential(this Credential credential)
        {
            return TableCredential.FromCredential(credential);
        }
    }
}