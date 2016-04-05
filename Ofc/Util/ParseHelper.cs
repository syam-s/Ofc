﻿using Ofc.Parsing.Hooks;
using OfcAlgorithm.Blocky.Integration;
using OfcAlgorithm.Integration.Dummy;
using OfcCore.Configurations;

namespace Ofc.Util
{
    using System;
    using System.IO;
    using JetBrains.Annotations;
    using Ofc.IO;
    using Ofc.Parsing;

    internal static class ParseHelper
    {
        internal static void ParseFile(string target, [CanBeNull] IParserHook<string> hook)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!File.Exists(target)) throw new FileNotFoundException("Could not find the specified file.", target);
            var stream = new FileInputStream(target);
            var lexer = new OfcLexer(stream, true);
            var parser = new OfcParser(lexer, hook);
            parser.Parse();
        }

        internal static bool TryParseFile(string target, [CanBeNull] IParserHook<string> hook)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            try
            {
                ParseFile(target, hook);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ParseDirectory(string target, [CanBeNull] IParserHook<string> hook, bool recursive)
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                ParseFile(file, hook);
        }

        internal static int TryParseDirectory(string target, [CanBeNull] IParserHook<string> hook, bool recursive)
        {
            var a = 0;
            foreach (var file in Directory.EnumerateFiles(target, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                if (TryParseFile(file, hook))
                    a++;
            return a;
        }

        private static void CompressFolderBlocky(string srcPath, string targetPath)
        {
            using (new FileStream(targetPath, FileMode.Create))
            {
            }
            foreach (var file in Directory.GetFiles(srcPath, "*", SearchOption.AllDirectories))
            {
                CompressFileBlocky(file, targetPath, FileMode.Append);
            }
        }

        private static void CompressFileBlocky(string srcPath, string targetPath, FileMode mode = FileMode.Create)
        {
            using (var str = new FileStream(targetPath, mode))
            {
                BlockyAlgorithm.SetBlockfindingDebugConsoleEnabled(false);
                try
                {
                    var lexi = new OfcLexer(new FileInputStream(srcPath));
                    var purser = new OfcParser(lexi, new AlgorithmHook(new BlockyAlgorithm(), str));
                    purser.Parse();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(srcPath + ": " + ex);
                }
            }
        }

        private static void DecompressFileBlocky(string srcPath, string targetPath)
        {
            using (var str = new FileStream(srcPath, FileMode.Open))
            {
                var item = 0;
                while (str.Position < str.Length - 1)
                {
                    new BlockyAlgorithm().Decompress(null, EmptyConfiguration.Instance, str, new DummyReporter(Path.GetDirectoryName(targetPath) + "\\" + item++ + "." + Path.GetFileName(targetPath)));
                }
            }
        }
    }
}