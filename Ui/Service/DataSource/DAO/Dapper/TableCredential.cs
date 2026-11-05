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


        public static TableCredential FromCredential(Credential credential)
        {
            var tableCredential = new TableCredential()
            {
                Id = Ulid.NewUlid().ToString(),
                Name = credential.Name,
                Hash = credential.GetHash(),
                Json = DataVault.SimpleEncrypt(JsonConvert.SerializeObject(credential)),
            };
            if (string.IsNullOrEmpty(tableCredential.Name))
            {
                tableCredential.Name = tableCredential.Id; // fallback to Id if Name is empty
            }
            return tableCredential;
        }

        public Credential? ToCredential()
        {
            try
            {
                var c = JsonConvert.DeserializeObject<Credential>(DataVault.DecryptOrReturnOriginalString(Json));
                if (c != null)
                {
                    c.DatabaseId = Id;
                    c.Name = Name;
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