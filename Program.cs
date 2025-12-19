using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PatchmanUnity
{
    public class Operation
    {
        public string? type { get; set; }
        public string? assetType { get; set; }
        public string? assetName { get; set; }
        public string? assetPath { get; set; }
    }

    public class OpsFile
    {
        public string? assetFilePath { get; set; }
        public Operation[]? operations { get; set; }
        public string? saveFilePath { get; set; }
    }
    class Program
    {
        static public AssetsManager manager = new();
        static public AssetsFileInstance? afileInst;
        static public AssetsFile? afile;
        static public bool changed = false;
        static int Main(string[] args)
        {

            if (args == null || args.Length == 0)
            {
                PrintUsage();
                return 1;
            }
            

            switch (args[0].ToLowerInvariant())
            {
                case "importasset":
                    return RunImportAsset(args) ? 0 : 2;

                case "extractbundle":
                    return RunExtractBundle(args) ? 0 : 3;
                case "automation":
                    return RunReadOps(args) ? 0 : 4;
                case "help":
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    PrintUsage();
                    return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  patcher.exe exportfrombundle <bundlePath> <outputDir>");
            Console.WriteLine("  patcher.exe importintobundle --bundlePath=\"exampleBundlePath\" --assetName=\"exampleAssetName\" --exportPath=\"exampleExportPath\" --importPath=\"exampleImportPath\"");
        }

        static bool RunExtractBundle(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("extractbundle <bundlepath> <extractpath>");
                return false;
            }

            if (!Directory.Exists(args[2]))
            {
                Console.WriteLine("Directory does not exist!");
                return false;
            }

            HashSet<string> flags = GetFlags(args);

            foreach (string file in Directory.EnumerateFiles(args[2]))
            {
                string decompFile = $"{file}.decomp";

                if (flags.Contains("-md"))
                    decompFile = null;

                if (!File.Exists(file))
                {
                    Console.WriteLine($"File {file} does not exist!");
                    return false;
                }

                DetectedFileType fileType = FileTypeDetector.DetectFileType(file);
                if (fileType != DetectedFileType.BundleFile)
                {
                    continue;
                }

                Console.WriteLine($"Decompressing {file}...");
                AssetBundleFile bun = DecompressBundle(file, decompFile);

                int entryCount = bun.BlockAndDirInfo.DirectoryInfos.Count;
                for (int i = 0; i < entryCount; i++)
                {
                    string name = bun.BlockAndDirInfo.DirectoryInfos[i].Name;
                    byte[] data = BundleHelper.LoadAssetDataFromBundle(bun, i);
                    string outName;
                    if (flags.Contains("-keepnames"))
                        outName = Path.Combine(args[2], name);
                    else
                        outName = Path.Combine(args[2], $"{Path.GetFileName(file)}_{name}");
                    Console.WriteLine($"Exporting {outName}...");
                    File.WriteAllBytes(outName, data);
                }

                bun.Close();

                if (!flags.Contains("-kd") && !flags.Contains("-md") && File.Exists(decompFile))
                    File.Delete(decompFile);

                Console.WriteLine("Done.");
            }

            return true;
        }

        static HashSet<string> GetFlags(string[] args)
        {
            HashSet<string> flags = new HashSet<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("-"))
                    flags.Add(args[i]);
            }
            return flags;
        }

        static AssetBundleFile DecompressBundle(string file, string? decompFile)
        {
            AssetBundleFile bun = new AssetBundleFile();

            Stream fs = File.OpenRead(file);
            AssetsFileReader r = new AssetsFileReader(fs);

            bun.Read(r);
            if (bun.Header.GetCompressionType() != 0)
            {
                Stream nfs;
                if (decompFile == null)
                    nfs = new MemoryStream();
                else
                    nfs = File.Open(decompFile, FileMode.Create, FileAccess.ReadWrite);

                AssetsFileWriter w = new AssetsFileWriter(nfs);
                bun.Unpack(w);

                nfs.Position = 0;
                fs.Close();

                fs = nfs;
                r = new AssetsFileReader(fs);

                bun = new AssetBundleFile();
                bun.Read(r);
            }

            return bun;
        }
        static bool RunReadOps(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("automation <operationsFilePath>");
                return false;
            }
            if (!File.Exists(args[1]))
            {
                return false;
            }
            OpsFile? ops;
            try
            {
                var json = File.ReadAllText(args[1]);
                var opts = new JsonSerializerOptions{ PropertyNameCaseInsensitive = true };
                ops = JsonSerializer.Deserialize<OpsFile>(json, opts);
                if (ops == null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read ops file: {ex.Message}");
                return false;
            }

            if (ops == null || string.IsNullOrEmpty(ops.assetFilePath) || ops.operations == null)
            {
                Console.Error.WriteLine("Invalid ops file contents.");
                return false;
            }
            RunHandleOps(ops);
            return true;
        }

        static bool RunHandleOps(OpsFile ops)
        {
            
            manager.LoadClassPackage("classdata.tpk");
            afileInst = manager.LoadAssetsFile(ops.assetFilePath, true);
            afile = afileInst.file;
            manager.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);

            RunImportAssetBatch(ops);

            if (changed)
            {
                using AssetsFileWriter writer = new(ops.saveFilePath);
                afile.Write(writer);                        
            }
            return true;
        }

        static bool RunImportAssetBatch(OpsFile ops)
        {
            if (afile == null) return false;
            
            foreach (var operation in ops.operations ?? [])
            {
                if (operation.type == "import")
                {
                    if (operation.assetType == "Texture2D")
                    {
                        foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.Texture2D))
                        {
                            var goBase = manager.GetBaseField(afileInst, goInfo);
                            var name = goBase["m_Name"].AsString;

                            if (name == operation.assetName)
                            {
                                var sResource = goBase["m_Resource"];
                                sResource["m_Source"].AsString = operation.assetPath;
                                goInfo.SetNewData(goBase);

                                changed = true;
                            }                       
                        }
                    } else if (operation.assetType == "AudioClip")
                    {
                        foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.AudioClip))
                        {
                            var goBase = manager.GetBaseField(afileInst, goInfo);
                            var name = goBase["m_Name"].AsString;

                            if (name == operation.assetName)
                            {
                                var sResource = goBase["m_Resource"];
                                sResource["m_Source"].AsString = operation.assetPath;
                                goInfo.SetNewData(goBase);

                                changed = true;
                                break;
                            }                       
                        }
                    }
                } 
            }
            return true;
        }

        static bool RunImportAsset(string[] args)
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("importasset <assetfilepath> <assetType> <assetName> <moddedAssetFileName> <savePath>");
                return false;
            }

            manager.LoadClassPackage("classdata.tpk");
            afileInst = manager.LoadAssetsFile(args[1], true);
            afile = afileInst.file;
            manager.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);

            if (args[2] == "AudioClip") {
                foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.AudioClip))
                {
                    var goBase = manager.GetBaseField(afileInst, goInfo);
                    var name = goBase["m_Name"].AsString;
                    if (name == args[3])
                    {
                        var sResource = goBase["m_Resource"];

                        sResource["m_Source"].AsString = args[4];

                        goInfo.SetNewData(goBase);
                        changed = true;

                    }

                    if (changed) 
                    {
                        using AssetsFileWriter writer = new(args[5]);
                        afile.Write(writer);
                        
                    }
                }
            } else if (args[2] == "Texture2D") {
                foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    var goBase = manager.GetBaseField(afileInst, goInfo);
                    var name = goBase["m_Name"].AsString;
                    if (name == args[3])
                    {
                        goBase["m_Source"].AsString = args[4]; 
                        goInfo.SetNewData(goBase);

                        changed = true;
                    }

                    
                    if (changed) 
                    {
                        using AssetsFileWriter writer = new(args[5]);
                        afile.Write(writer);                        
                    }
                }
            }
            return true;
        }

        
    }
}

namespace PatchmanUnity
{
    public static class FileTypeDetector
    {
        public static DetectedFileType DetectFileType(string filePath)
        {
            using (FileStream fs = File.OpenRead(filePath))
            using (AssetsFileReader r = new AssetsFileReader(fs))
            {
                return DetectFileType(r, 0);
            }
        }

        public static DetectedFileType DetectFileType(AssetsFileReader r, long startAddress)
        {
            string possibleBundleHeader;
            int possibleFormat;
            string emptyVersion, fullVersion;

            r.BigEndian = true;

            if (r.BaseStream.Length < 0x20)
            {
                return DetectedFileType.Unknown;
            }
            r.Position = startAddress;
            possibleBundleHeader = r.ReadStringLength(7);
            r.Position = startAddress + 0x08;
            possibleFormat = r.ReadInt32();

            r.Position = startAddress + (possibleFormat >= 0x16 ? 0x30 : 0x14);

            string possibleVersion = "";
            char curChar;
            while (r.Position < r.BaseStream.Length && (curChar = (char)r.ReadByte()) != 0x00)
            {
                possibleVersion += curChar;
                if (possibleVersion.Length > 0xFF)
                {
                    break;
                }
            }
            emptyVersion = Regex.Replace(possibleVersion, "[a-zA-Z0-9\\.\\n\\-]", "");
            fullVersion = Regex.Replace(possibleVersion, "[^a-zA-Z0-9\\.\\n\\-]", "");

            if (possibleBundleHeader == "UnityFS")
            {
                return DetectedFileType.BundleFile;
            }
            else if (possibleFormat < 0xFF && emptyVersion.Length == 0 && fullVersion.Length >= 5)
            {
                return DetectedFileType.AssetsFile;
            }
            return DetectedFileType.Unknown;
        }
    }

    public enum DetectedFileType
    {
        Unknown,
        AssetsFile,
        BundleFile
    }
}