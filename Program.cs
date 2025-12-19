using System;
using System.IO;
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
                case "automation":
                    return RunReadOps(args) ? 0 : 3;
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