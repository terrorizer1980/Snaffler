﻿using SnaffCore.Classifiers;
using SnaffCore.Concurrency;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using static SnaffCore.Config.Options;
using SnaffCore.Classifiers.EffectiveAccess;

namespace Classifiers
{
    public class FileClassifier
    {
        private ClassifierRule ClassifierRule { get; set; }

        public FileClassifier(ClassifierRule inRule)
        {
            this.ClassifierRule = inRule;
        }

        public bool ClassifyFile(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            // figure out what part we gonna look at
            string stringToMatch = null;

            switch (ClassifierRule.MatchLocation)
            {
                case MatchLoc.FileExtension:
                    stringToMatch = fileInfo.Extension;
                    // special handling to treat files named like 'thing.kdbx.bak'
                    if (stringToMatch == ".bak")
                    {
                        // strip off .bak
                        string subName = fileInfo.Name.Replace(".bak", "");
                        stringToMatch = Path.GetExtension(subName);
                        // if this results in no file extension, put it back.
                        if (stringToMatch == "")
                        {
                            stringToMatch = ".bak";
                        }
                    }
                    // this is insane that i have to do this but apparently files with no extension return
                    // this bullshit
                    if (stringToMatch == "")
                    {
                        return false;
                    }
                    break;
                case MatchLoc.FileName:
                    stringToMatch = fileInfo.Name;
                    break;
                case MatchLoc.FilePath:
                    stringToMatch = fileInfo.FullName;
                    break;
                case MatchLoc.FileLength:
                    if (!SizeMatch(fileInfo))
                    {
                        return false;
                    }
                    else break;
                default:
                    Mq.Error("You've got a misconfigured file classifier rule named " + ClassifierRule.RuleName + ".");
                    return false;
            }

            TextResult textResult = null;

            if (!String.IsNullOrEmpty(stringToMatch))
            {
                TextClassifier textClassifier = new TextClassifier(ClassifierRule);
                // check if it matches
                textResult = textClassifier.TextMatch(stringToMatch);
                if (textResult == null)
                {
                    // if it doesn't we just bail now.
                    return false;
                }
            }

            FileResult fileResult;
            // if it matches, see what we're gonna do with it
            switch (ClassifierRule.MatchAction)
            {
                case MatchAction.Discard:
                    // chuck it.
                    return true;
                case MatchAction.Snaffle:
                    // snaffle that bad boy
                    fileResult = new FileResult(fileInfo)
                    {
                        MatchedRule = ClassifierRule,
                        TextResult = textResult
                    };
                    // if the file was list-only, don't bother sending a result back to the user.
                    if (!fileResult.RwStatus.CanRead && !fileResult.RwStatus.CanModify && !fileResult.RwStatus.CanWrite) { return false; };
                    Mq.FileResult(fileResult);
                    return false;
                case MatchAction.CheckForKeys:
                    // do a special x509 dance
                    List<string> x509MatchReason = x509Match(fileInfo);
                    if (x509MatchReason.Count >= 0)
                    {
                        // if there were any matchreasons, cat them together...
                        string matchContext = String.Join(",", x509MatchReason);
                        // and sling the results on the queue
                        fileResult = new FileResult(fileInfo)
                        {
                            MatchedRule = ClassifierRule,
                            TextResult = new TextResult()
                            {
                                MatchContext = matchContext,
                                MatchedStrings = new List<string>() { "" }
                            }
                        };

                        if (!fileResult.RwStatus.CanRead && !fileResult.RwStatus.CanModify && !fileResult.RwStatus.CanWrite) { return false; };

                        Mq.FileResult(fileResult);
                    }
                    return false;
                case MatchAction.Relay:
                    // bounce it on to the next ClassifierRule
                    try
                    {
                        bool fLoggedContentSizeWarning = false;

                        foreach (string relayTarget in ClassifierRule.RelayTargets)
                        {
                            ClassifierRule nextRule =
                                MyOptions.ClassifierRules.First(thing => thing.RuleName == relayTarget);

                            if (nextRule.EnumerationScope == EnumerationScope.ContentsEnumeration)
                            {
                                if (fileInfo.Length > MyOptions.MaxSizeToGrep)
                                {
                                    if(!fLoggedContentSizeWarning)
                                    {
                                        // Just log once per relay rule, no need to fill up the log with one for each relay target
                                        Mq.Trace("The following file was bigger than the MaxSizeToGrep config parameter:" + fileInfo.FullName);
                                        fLoggedContentSizeWarning = true;
                                    }
                                    
                                    continue;
                                }

                                ContentClassifier nextContentClassifier = new ContentClassifier(nextRule);
                                nextContentClassifier.ClassifyContent(fileInfo);
                            }
                            else if (nextRule.EnumerationScope == EnumerationScope.FileEnumeration)
                            {
                                FileClassifier nextFileClassifier = new FileClassifier(nextRule);
                                nextFileClassifier.ClassifyFile(fileInfo);
                            }
                            else
                            {
                                Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                            }
                        }
                        return false;
                    }
                    catch (IOException e)
                    {
                        Mq.Trace(e.ToString());
                    }
                    catch (Exception e)
                    {
                        Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                        Mq.Trace(e.ToString());
                    }
                    return false;
                case MatchAction.EnterArchive:
                    // do a special looking inside archive files dance using
                    // https://github.com/adamhathcock/sharpcompress
                    // TODO FUUUUUCK
                    throw new NotImplementedException("Haven't implemented walking dir structures inside archives.");
                default:
                    Mq.Error("You've got a misconfigured file ClassifierRule named " + ClassifierRule.RuleName + ".");
                    return false;
            }
        }
        public bool SizeMatch(FileInfo fileInfo)
        {
            if (this.ClassifierRule.MatchLength == fileInfo.Length)
            {
                return true;
            }
            return false;
        }

        public X509Certificate2 parseCert(string certPath, string password = null)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            // IT TURNS OUT THAT new X509Certificate2() actually writes a file to a temp path and if you
            // don't manage it yourself it hits 65,000 temp files and hangs.

            var tempfile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            File.Copy(certPath, tempfile);
            X509Certificate2 parsedCert = null;

            try
            {
                if (Path.GetExtension(certPath) == ".pem")
                {
                    string pemstring = File.ReadAllText(tempfile);
                    byte[] certBuffer = Helpers.GetBytesFromPEM(pemstring, PemStringType.Certificate);
                    byte[] keyBuffer = Helpers.GetBytesFromPEM(pemstring, PemStringType.RsaPrivateKey);

                    if (certBuffer != null)
                    {
                        parsedCert = new X509Certificate2(certBuffer);
                        if (keyBuffer != null)
                        {
                            RSACryptoServiceProvider prov = Crypto.DecodeRsaPrivateKey(keyBuffer);
                            parsedCert.PrivateKey = prov;
                        }
                    }
                    else
                    {
                        Mq.Error("Failure parsing " + certPath);
                    }
                }
                else
                {
                    parsedCert = new X509Certificate2(tempfile, password);
                }
            }
            catch (Exception e)
            {
                File.Delete(tempfile);
                throw e;
            }

            File.Delete(tempfile);

            return parsedCert;
        }

        public List<string> x509Match(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            string certPath = fileInfo.FullName;
            List<string> matchReasons = new List<string>();
            X509Certificate2 parsedCert = null;
            bool nopwrequired = false;

            // TODO - handle if there is no private key, strip out unnecessary stuff from Certificate.cs, make work with pfx style stuff below

            try
            {
                // try to parse it, it'll throw if it needs a password
                parsedCert = parseCert(certPath);
                
                // if it parses we know we didn't need a password
                nopwrequired = true;
            }
            catch (CryptographicException e)
            {
                // if it doesn't parse that almost certainly means we need a password
                Mq.Trace(e.ToString());

                // build the list of passwords to try including the filename
                List<string> passwords = MyOptions.CertPasswords;
                passwords.Add(Path.GetFileNameWithoutExtension(fileInfo.Name));

                // try each of our very obvious passwords
                foreach (string password in MyOptions.CertPasswords)
                {
                    try
                    {
                        parsedCert = parseCert(certPath, password);
                        if (password == "")
                        {
                            matchReasons.Add("PasswordBlank");
                        }
                        else
                        {
                            matchReasons.Add("PasswordCracked: " + password);
                        }
                    }
                    catch (CryptographicException ee)
                    {
                        Mq.Trace("Password " + password + " invalid for cert file " + fileInfo.FullName + " " + ee.ToString());
                    }
                }
                if (matchReasons.Count == 0) 
                {
                    matchReasons.Add("HasPassword");
                    matchReasons.Add("LookNearbyFor.txtFiles");
                }
            }
            catch (Exception e)
            {
                Mq.Error("Unhandled exception parsing cert: " + fileInfo.FullName + " " + e.ToString());
            }

            if (parsedCert != null)
            {
                // check if it includes a private key, if not, who cares?
                if (parsedCert.HasPrivateKey)
                {
                    matchReasons.Add("HasPrivateKey");

                    if (nopwrequired) { matchReasons.Add("NoPasswordRequired"); }

                    matchReasons.Add("Subject:" + parsedCert.Subject);

                    // take a look at the extensions
                    X509ExtensionCollection extensions = parsedCert.Extensions;

                    // this feels dumb but whatever
                    foreach (X509Extension extension in extensions)
                    {
                        AsnEncodedData asndata = new AsnEncodedData(extension.Oid, extension.RawData);
                        string asndataString = asndata.Format(false);
                        if (extension.Oid.FriendlyName == "Basic Constraints")
                        {
                            if (asndataString.Contains("Subject Type=CA"))
                            {
                                matchReasons.Add("IsCACert");
                            }
                        }
                        if (extension.GetType() == typeof(X509KeyUsageExtension))
                        {
                            matchReasons.Add((extension as X509KeyUsageExtension).KeyUsages.ToString());
                        }
                        if (extension.GetType() == typeof(X509EnhancedKeyUsageExtension))
                        {
                            List<string> ekus = new List<string>();

                            X509EnhancedKeyUsageExtension ekuExtension = (X509EnhancedKeyUsageExtension)extension;
                            foreach (Oid eku in ekuExtension.EnhancedKeyUsages)
                            {
                                ekus.Add(eku.FriendlyName);
                            }
                            // include the EKUs in the info we're passing to the user
                            string ekustring = String.Join("|", ekus);
                            matchReasons.Add(ekustring);
                        };
                        if (extension.Oid.FriendlyName == "Subject Alternative Name")
                        {
                            byte[] sanbytes = extension.RawData;
                            string san = Encoding.UTF8.GetString(sanbytes, 0, sanbytes.Length);
                            matchReasons.Add(asndataString);
                        }
                    }

                    matchReasons.Add("Expiry:" + parsedCert.GetExpirationDateString());
                    matchReasons.Add("Issuer:" + parsedCert.Issuer);
                }
            }
            return matchReasons;
        }
    }

    public class FileResult
    {
        public FileInfo FileInfo { get; set; }
        public TextResult TextResult { get; set; }
        public RwStatus RwStatus { get; set; }
        public ClassifierRule MatchedRule { get; set; }

        public FileResult(FileInfo fileInfo)
        {
            //EffectivePermissions effPerms = new EffectivePermissions(MyOptions.CurrentUser);

            // get an aggressively simplified version of the file's ACL
            //this.RwStatus = effPerms.CanRw(fileInfo);
            try
            {
                File.OpenRead(fileInfo.FullName);
                this.RwStatus = new RwStatus() { CanRead = true, CanModify = false, CanWrite = false };
            }
            catch (Exception e)
            {
                this.RwStatus = new RwStatus() { CanModify = false, CanRead = false, CanWrite = false };
            }

            // nasty debug
            this.FileInfo = fileInfo;

            // this is where we actually automatically grab a copy of the file if wanted.
            if (MyOptions.Snaffle)
            {
                if ((MyOptions.MaxSizeToSnaffle >= fileInfo.Length) && RwStatus.CanRead)
                {
                    SnaffleFile(fileInfo, MyOptions.SnafflePath);
                }
            }
        }

        public void SnaffleFile(FileInfo fileInfo, string snafflePath)
        {
            string sourcePath = fileInfo.FullName;
            // clean it up and normalise it a bit
            string cleanedPath = sourcePath.Replace(':', '.').Replace('$', '.').Replace("\\\\", "\\");
            //string cleanedPath = Path.GetFullPath(sourcePath.Replace(':', '.').Replace('$', '.'));
            // make the dir exist
            string snaffleFilePath = Path.Combine(snafflePath, cleanedPath);
            string snaffleDirPath = Path.GetDirectoryName(snaffleFilePath);
            Directory.CreateDirectory(snaffleDirPath);
            File.Copy(sourcePath, (Path.Combine(snafflePath, cleanedPath)), true);
        }

        /*
        public static EffectivePermissions.RwStatus CanRw(FileInfo fileInfo)
        {
            BlockingMq Mq = BlockingMq.GetMq();

            try
            {
                EffectivePermissions.RwStatus rwStatus = new EffectivePermissions.RwStatus { CanWrite = false, CanRead = false, CanModify = false };
                EffectivePermissions effPerms = new EffectivePermissions();
                string dir = fileInfo.DirectoryName;

                // we hard code this otherwise it tries to do some madness where it uses RPC with a share server to check file access, then fails if you're not admin on that host.
                string hostname = "localhost";

                string whoami = WindowsIdentity.GetCurrent().Name;

                string[] accessStrings = effPerms.GetEffectivePermissions(fileInfo, whoami);

                string[] readRights = new string[] { "Read", "ReadAndExecute", "ReadData", "ListDirectory" };
                string[] writeRights = new string[] { "Write", "Modify", "FullControl", "TakeOwnership", "ChangePermissions", "AppendData", "WriteData", "CreateFiles", "CreateDirectories" };
                string[] modifyRights = new string[] { "Modify", "FullControl", "TakeOwnership", "ChangePermissions" };

                foreach (string access in accessStrings)
                {
                    if (access == "FullControl")
                    {
                        rwStatus.CanModify = true;
                        rwStatus.CanRead = true;
                        rwStatus.CanWrite = true;
                    }
                    if (readRights.Contains(access)){
                        rwStatus.CanRead = true;
                    }
                    if (writeRights.Contains(access))
                    {
                        rwStatus.CanWrite = true;
                    }
                    if (modifyRights.Contains(access))
                    {
                        rwStatus.CanModify = true;
                    }
                }

                return rwStatus;
            }
            catch (Exception e)
            {
                Mq.Error(e.ToString());
                return new EffectivePermissions.RwStatus { CanWrite = false, CanRead = false }; ;
            }
        }
        */
    }
}