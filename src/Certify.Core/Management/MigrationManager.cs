﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Certify.Management;
using Certify.Models;
using Certify.Models.Config;

namespace Certify.Core.Management
{
    public class ImportExportContent
    {
        public List<ManagedCertificate> ManagedCertificates { get; set; }
        public List<EncryptedContent> CertificateFiles { get; set; }
        public List<StoredCredential> StoredCredentials { get; set; }
        public List<CertificateAuthority> CertificateAuthorities { get; set; }
    }

    public class ImportExportPackage
    {
        public int FormatVersion { get; set; } = 1;
        public string Description { get; set; } = "Certify The Web - Exported App Settings";
        public string SourceName { get; set; }

        public DateTime ExportDate { get; set; }

        public ImportExportContent Content { get; set; }
    }

    public class EncryptedContent
    {
        public string Filename { get; set; }
        public byte[] Content { get; set; }
        public string Scheme { get; set; }
    }

    public class ExportSettings
    {
        public bool ExportAllStoredCredentials { get; set; }
        public string EncryptionSecret { get; set; }
    }

    public class ImportSettings
    {
        public string EncryptionSecret { get; set; }
    }

    /// <summary>
    /// Perform/preview import and export
    /// </summary>
    public class MigrationManager
    {
        private IItemManager _itemManager;
        private ICredentialsManager _credentialsManager;

        public MigrationManager(IItemManager itemManager, ICredentialsManager credentialsManager)
        {
            _itemManager = itemManager;
            _credentialsManager = credentialsManager;
        }

        /// <summary>
        /// Export the managed certificates and related settings for the given filter
        /// </summary>
        /// <param name="filter"></param>
        /// <returns>Package of exported settings</returns>
        public async Task<ImportExportPackage> GetExportPackage(ManagedCertificateFilter filter, ExportSettings settings, bool isPreview)
        {
            var export = new ImportExportPackage
            {
                SourceName = Environment.MachineName,
                ExportDate = DateTime.Now
            };

            // export managed certs, related certificate files, stored credentials

            // deployment tasks with local script or path references will need to copy the scripts separately. Need a summary of items to copy.

            var managedCerts = await _itemManager.GetAll(filter);

            export.Content = new ImportExportContent
            {
                ManagedCertificates = managedCerts,
                CertificateFiles = new List<EncryptedContent>(),
                CertificateAuthorities = new List<CertificateAuthority>(),
                StoredCredentials = new List<StoredCredential>()
            };


            // for each managed cert, export the current certificate files (if present)
            foreach (var c in managedCerts)
            {
                if (!string.IsNullOrEmpty(c.CertificatePath))
                {
                    var certBytes = System.IO.File.ReadAllBytes(c.CertificatePath);

                    var encryptedBytes = EncryptBytes(certBytes, settings.EncryptionSecret);
                    var content = new EncryptedContent { Filename = c.CertificatePath, Scheme = "Default", Content = encryptedBytes };

                    export.Content.CertificateFiles.Add(content);
                }
            }


            // for each managed cert, check used stored credentials (DNS challenges or deployment tasks)
            var allCredentials = await _credentialsManager.GetCredentials();
            var usedCredentials = new List<StoredCredential>();

            if (settings.ExportAllStoredCredentials)
            {
                usedCredentials.AddRange(allCredentials);
            }
            else
            {
                foreach (var c in managedCerts)
                {
                    // gather credentials used by cert 
                    if (c.CertificatePasswordCredentialId != null)
                    {
                        if (!usedCredentials.Any(u => u.StorageKey == c.CertificatePasswordCredentialId))
                        {
                            usedCredentials.Add(allCredentials.Find(a => a.StorageKey == c.CertificatePasswordCredentialId));
                        }
                    }

                    // gather credentials used by tasks
                    var allTasks = new List<Config.DeploymentTaskConfig>();

                    if (c.PreRequestTasks != null)
                    {
                        allTasks.AddRange(c.PreRequestTasks);
                    }

                    if (c.PostRequestTasks != null)
                    {
                        allTasks.AddRange(c.PostRequestTasks);
                    }

                    if (allTasks.Any())
                    {

                        /*var usedTaskCredentials = allTasks
                            .SelectMany(t => t.Parameters?.Select(p => p.Value))
                            .Distinct()
                            .Where(t => allCredentials.Any(ac => ac.StorageKey == t))
                            .ToList();*/
                        var usedTaskCredentials = allTasks
                            .Select(t => t.ChallengeCredentialKey)
                            .Distinct()
                            .Where(t => allCredentials.Any(ac => ac.StorageKey == t));

                        foreach (var used in usedTaskCredentials)
                        {
                            if (!usedCredentials.Any(u => u.StorageKey == used))
                            {
                                usedCredentials.Add(allCredentials.FirstOrDefault(u => u.StorageKey == used));
                            }
                        }
                    }
                }
            }

            // decrypt each used stored credential, re-encrypt and base64 encode secret
            foreach (var c in usedCredentials)
            {
                var decrypted = await _credentialsManager.GetUnlockedCredential(c.StorageKey);
                if (decrypted != null)
                {
                    var encBytes = EncryptBytes(Encoding.UTF8.GetBytes(decrypted), settings.EncryptionSecret);
                    c.Secret = Convert.ToBase64String(encBytes);
                }
            }

            export.Content.StoredCredentials = usedCredentials;

            // for each managed cert, check and summarise used local scripts

            // copy acme-dns settings

            // export acme accounts?
            return export;
        }

        private byte[] EncryptBytes(byte[] source, string secret)
        {
            var rmCrypto = new RijndaelManaged();

            byte[] key = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };
            byte[] iv = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };

            rmCrypto.Padding = PaddingMode.PKCS7;
            using (var memoryStream = new MemoryStream())
            using (var cryptoStream = new CryptoStream(memoryStream, rmCrypto.CreateEncryptor(key, iv), CryptoStreamMode.Write))
            {

                cryptoStream.Write(source, 0, source.Length);
                cryptoStream.FlushFinalBlock();
                cryptoStream.Close();
                return memoryStream.ToArray();
            }
        }

        private byte[] DecryptBytes(byte[] source, string secret)
        {
            using (RijndaelManaged rmCrypto = new RijndaelManaged())
            {

                byte[] key = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };
                byte[] iv = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };

                rmCrypto.Padding = PaddingMode.PKCS7;


                using (var decryptor = rmCrypto.CreateDecryptor(key, iv))
                {
                    using (var memoryStream = new MemoryStream(source))
                    {
                        using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read))
                        {
                            var decryptedBytes = new byte[source.Length];
                            var decryptedByteCount = cryptoStream.Read(decryptedBytes, 0, decryptedBytes.Length);
                            memoryStream.Close();
                            cryptoStream.Close();

                            return decryptedBytes;
                        }
                    }
                }

            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="package"></param>
        /// <param name="isPreviewMode"></param>
        /// <returns></returns>
        public async Task<List<ActionStep>> PerformImport(ImportExportPackage package, ImportSettings settings, bool isPreviewMode)
        {
            // apply import
            var steps = new List<ActionStep>();

            // import managed certs, certificate files, stored credentials, CAs

            // stored credentials
            var credentialImportSteps = new List<ActionStep>();
            foreach (var c in package.Content.StoredCredentials)
            {
                var decodedBytes =   Convert.FromBase64String(c.Secret);
                var decryptedBytes = DecryptBytes(decodedBytes, settings.EncryptionSecret);

                // convert decrypted bytes to UTF8 string and trim NUL 
                c.Secret = UTF8Encoding.UTF8.GetString(decryptedBytes).Trim('\0');

                if (!isPreviewMode)
                {
                    // perform actual import
                }

                credentialImportSteps.Add(new ActionStep { Title = c.Title, Key = c.StorageKey });
            }
            steps.Add(new ActionStep { Title = "Import Stored Credentials", Category = "Import", Substeps = credentialImportSteps, Key = "StoredCredentials" });


            // managed certs
            var managedCertImportSteps = new List<ActionStep>();
            foreach (var c in package.Content.ManagedCertificates)
            {
                if (!isPreviewMode)
                {
                    // perform actual import
                }

                managedCertImportSteps.Add(new ActionStep { Title = c.Name, Key = c.Id });
            }
            steps.Add(new ActionStep { Title = "Import Managed Certificates", Category = "Import", Substeps = managedCertImportSteps, Key = "ManagedCerts" });

            // certificate files
            var certFileImportSteps = new List<ActionStep>();
            foreach (var c in package.Content.CertificateFiles)
            {
                var pfxBytes = DecryptBytes(c.Content, settings.EncryptionSecret);

                var cert = new X509Certificate2(pfxBytes);


                if (!isPreviewMode)
                {
                    // perform actual import
                    cert.Verify();
                }
                else
                {
                    // verify cert decrypt
                    cert.Verify();
                }

                certFileImportSteps.Add(new ActionStep { Title = $"Importing PFX {cert.Subject}, expiring {cert.NotAfter}", Key = c.Filename });
            }

            steps.Add(new ActionStep { Title = "Import Certificate Files", Category = "Import", Substeps = certFileImportSteps, Key = "CertFiles" });

            // store and apply current certificates to bindings
            return steps;
        }
    }
}
