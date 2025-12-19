using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Runtime.CompilerServices;
using System.Data;

namespace PatchmanUnity
{
    public class Operation
    {
        public string? Type { get; set; }
        public string? AssetType { get; set; }
        public string? AssetName { get; set; }
        public string? AssetPath { get; set; }
    }

    public class OpsFile
    {
        public string? OriginalFilePath { get; set; }
        public Operation[]? Operations { get; set; }
        public string? ModifiedFilePath { get; set; }
    }
    class Program
    {
        static public AssetsManager manager = new();
        static public AssetBundleFile? bun;
        static public BundleFileInstance? bunInst;
        static public OpsFile? ops;
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
                case "batchimportassets":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("batchimportassets <operationsFilePath>");
                        return 4;
                    }
                    ops = RunReadOps(args[1]);
                    return RunHandleOps() ? 0:4;

                    case "batchimportbundle":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("batchimportbundle <operationsFilePath>");
                        return 5;
                    }
                    ops = RunReadOps(args[1]);
                    return RunBatchImportBundle() ? 0:5;
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
                    decompFile = string.Empty;

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

        static bool RunBatchImportBundle()
        {
            manager = new AssetsManager();

            var fileIndex = 0;
            bunInst = manager.LoadBundleFile(ops?.OriginalFilePath ?? throw new ArgumentNullException("ops.OriginalFilePath"), true);
            bun = bunInst.file;
            afileInst = manager.LoadAssetsFileFromBundle(bunInst, fileIndex, false);
            afile = afileInst.file;


            HandleImportBatch(ops);

            if (changed) {
                bun.BlockAndDirInfo.DirectoryInfos[fileIndex].SetNewData(afile);
                using (AssetsFileWriter writer = new AssetsFileWriter(ops.ModifiedFilePath + ".uncompressed"))
                {
                    bun.Write(writer);
                }
            }
            
            if (string.IsNullOrEmpty(ops?.ModifiedFilePath))
            {
                throw new ArgumentNullException("ops.ModifiedFilePath", "The save file path cannot be null or empty.");
            }

            CompressBundle(ops.ModifiedFilePath);
            return true;
        }

        static bool CompressBundle(string filePath)
        {
            var uncompressedName = filePath + ".uncompressed";

            var newUncompressedBundle = new AssetBundleFile();
            newUncompressedBundle.Read(new AssetsFileReader(File.OpenRead(uncompressedName)));

            using (AssetsFileWriter writer = new AssetsFileWriter(filePath))
            {
                newUncompressedBundle.Pack(writer, AssetBundleCompressionType.LZ4);
            }

            newUncompressedBundle.Close();

            return true;
        }


        static OpsFile RunReadOps(string filepath)
        {

            if (!File.Exists(filepath))
            {
                return new OpsFile();
            }
            OpsFile? ops;
            try
            {
                var json = File.ReadAllText(filepath);
                var opts = new JsonSerializerOptions{ PropertyNameCaseInsensitive = true };
                ops = JsonSerializer.Deserialize<OpsFile>(json, opts);
                if (ops == null)
                {
                    return new OpsFile();

                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read ops file: {ex.Message}");
                return new OpsFile();

            }

            if (ops == null || string.IsNullOrEmpty(ops.OriginalFilePath) || ops.Operations == null)
            {
                Console.Error.WriteLine("Invalid ops file contents.");
                return new OpsFile();

            }
            return ops;
        }

        static bool RunHandleOps()
        {
            
            manager.LoadClassPackage("classdata.tpk");
            afileInst = manager.LoadAssetsFile(ops?.OriginalFilePath ?? throw new ArgumentNullException("ops.OriginalFilePath"), true);
            afile = afileInst.file;
            manager.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);

            HandleImportBatch(ops);

            if (changed)
            {
                using AssetsFileWriter writer = new(ops.ModifiedFilePath);
                afile.Write(writer);                        
            }
            return true;
        }

        static bool HandleImportBatch(OpsFile ops)
        {
            if (afile == null) return false;
            
            foreach (var operation in ops.Operations ?? [])
            {
                if (operation.Type == "import")
                {
                    if (operation.AssetType == "Texture2D")
                    {
                        foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.Texture2D))
                        {
                            var goBase = manager.GetBaseField(afileInst, goInfo);
                            var name = goBase["m_Name"].AsString;

                            if (name == operation.AssetName)
                            {
                                var sResource = goBase["m_Resource"];
                                sResource["m_Source"].AsString = operation.AssetPath;
                                goInfo.SetNewData(goBase);

                                changed = true;
                                break;
                            }                       
                        }
                    } else if (operation.AssetType == "AudioClip")
                    {
                        foreach (var goInfo in afile.GetAssetsOfType(AssetClassID.AudioClip))
                        {
                            var goBase = manager.GetBaseField(afileInst, goInfo);
                            var name = goBase["m_Name"].AsString;

                            if (name == operation.AssetName)
                            {
                                var sResource = goBase["m_Resource"];
                                sResource["m_Source"].AsString = operation.AssetPath;
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
                Console.Error.WriteLine("importasset <OriginalFilePath> <AssetType> <assetName> <moddedAssetFileName> <savePath>");
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

