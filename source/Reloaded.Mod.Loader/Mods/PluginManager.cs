﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using Reloaded.Mod.Loader.Exceptions;
using Reloaded.Mod.Loader.IO.Structs;
using Reloaded.Mod.Loader.Mods.Structs;
using Reloaded.Mod.Loader.Server.Messages.Structures;
using Reloaded.Mod.Loader.Utilities;
using static Reloaded.Mod.Loader.Utilities.LogMessageFormatter;

namespace Reloaded.Mod.Loader.Mods
{
    /// <summary>
    /// A general loader based on <see cref="McMaster.NETCore.Plugins"/> that loads individual mods as plugins.
    /// </summary>
    public class PluginManager : IDisposable
    {
        public LoaderAPI LoaderApi { get; }
        private static readonly Type[] DefaultExportedTypes = new Type[0];
        private static readonly Type[] SharedTypes = { typeof(IModLoader), typeof(IMod) };

        private readonly ConcurrentDictionary<string, ModInstance> _modifications = new ConcurrentDictionary<string, ModInstance>();
        private readonly ConcurrentDictionary<string, ModAssemblyMetadata> _modIdToMetadata = new ConcurrentDictionary<string, ModAssemblyMetadata>();  
        private readonly ConcurrentDictionary<string, string> _modIdToFolder  = new ConcurrentDictionary<string, string>(); // Maps Mod ID to folder containing mod.

        private LoadContext _sharedContext;
        private readonly Loader _loader;

        /// <summary>
        /// Initializes the <see cref="PluginManager"/>
        /// </summary>
        /// <param name="loader">Instance of the mod loader.</param>
        /// <param name="sharedContext">Used only for testing. Sets shared load context used for plugins.</param>
        public PluginManager(Loader loader, LoadContext sharedContext = null)
        {
            _loader = loader;
            LoaderApi = new LoaderAPI(_loader);
            _sharedContext = sharedContext ?? LoadContext.BuildSharedLoadContext();
        }

        public void Dispose()
        {
            foreach (var modification in _modifications.Values)
            {
                modification.Dispose();
            }
        }

        /// <summary>
        /// Retrieves a list of all loaded modifications.
        /// </summary>
        public IReadOnlyCollection<ModInstance> GetModifications() => (IReadOnlyCollection<ModInstance>) _modifications.Values;

        /// <summary>
        /// Retrieves the directory of a mod with a specific Mod ID.
        /// </summary>
        /// <param name="modId">The mod id of the mod.</param>
        /// <returns>The directory containing the mod.</returns>
        public string GetDirectoryForModId(string modId)
        {
            return _modIdToFolder[modId];
        }

        /// <summary>
        /// Returns true if a mod is loaded, else false.
        /// </summary>
        public bool IsModLoaded(string modId)
        {
            return _modifications.ContainsKey(modId);
        }

        /// <summary>
        /// Loads a collection of mods.
        /// </summary>
        /// <param name="modPaths">Tuples of individual mod configurations and the paths to those configurations.</param>
        public void LoadMods(List<PathGenericTuple<IModConfig>> modPaths)
        {
            /* Load mods. */
            if (modPaths.Count > 0)
            {
                PreloadAssemblyMetadata(modPaths);
                var modInstances = ExecuteWithStopwatch($"Prepare All Mods (Total)", PrepareAllMods, modPaths);
                ExecuteWithStopwatch($"Initialized All Mods (Total)", StartAllInstances, modInstances);
            }
        }

        private ModInstance[] PrepareAllMods(List<PathGenericTuple<IModConfig>> modPaths)
        {
            var partitioner = Partitioner.Create(0, modPaths.Count);
            var modInstances = new ModInstance[modPaths.Count];

            Parallel.ForEach(partitioner, (range, loopState) =>
            {
                // Loop over each range element without a delegate invocation.
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var modPath = modPaths[i];
                    modInstances[i] = ExecuteWithStopwatch($"Prepared Mod: {modPath.Object.ModId}", GetModInstance, modPath);
                }
            });

            return modInstances;
        }

        private void StartAllInstances(ModInstance[] instances)
        {
            foreach (var instance in instances)
                ExecuteWithStopwatch($"Initialized Mod: {instance.ModConfig.ModId}", StartMod, instance);
        }

        /// <summary>
        /// Unloads an individual mod.
        /// </summary>
        public void UnloadMod(string modId)
        {
            var mod = _modifications[modId];
            if (mod != null)
            {
                if (mod.CanUnload)
                {
                    LoaderApi.ModUnloading(mod.Mod, mod.ModConfig);
                    _modIdToFolder.Remove(modId, out _);
                    _modIdToMetadata.Remove(modId, out _);
                    _modifications.Remove(modId, out _);
                    mod.Dispose();
                }
                else
                {
                    throw new ReloadedException(Errors.ModUnloadNotSupported(modId));
                }
            }
            else
            {
                throw new ReloadedException(Errors.ModToUnloadNotFound(modId));
            }
        }

        /// <summary>
        /// Suspends an individual mod.
        /// </summary>
        public void SuspendMod(string modId)
        {
            var mod = _modifications[modId];
            if (mod != null)
            {
                if (mod.CanSuspend)
                {
                    mod.Suspend();
                }
                else
                {
                    throw new ReloadedException(Errors.ModSuspendNotSupported(modId));
                }
            }
            else
            {
                throw new ReloadedException(Errors.ModToSuspendNotFound(modId));
            }
        }

        /// <summary>
        /// Resumes an individual mod.
        /// </summary>
        public void ResumeMod(string modId)
        {
            var mod = _modifications[modId];
            if (mod != null)
            {
                if (mod.CanSuspend)
                {
                    mod.Resume();
                }
                else
                {
                    throw new ReloadedException(Errors.ModSuspendNotSupported(modId));
                }
            }
            else
            {
                throw new ReloadedException(Errors.ModToResumeNotFound(modId));
            }
        }

        /// <summary>
        /// Returns a summary of all of the loaded mods in this process.
        /// </summary>
        public ModInstance[] GetLoadedMods()
        {
            return _modifications.Values.ToArray();
        }

        /// <summary>
        /// Returns a summary of all of the loaded mods in this process.
        /// </summary>
        public List<ModInfo> GetLoadedModSummary()
        {
            var allModInfo = new List<ModInfo>();

            foreach (var entry in _modifications)
                allModInfo.Add(new ModInfo(entry.Value.State, entry.Key, entry.Value.CanSuspend, entry.Value.CanUnload));

            return allModInfo;
        }

        /* Mod Loading */

        /// <summary>
        /// Obtains an instance of an individual ready to load mod.
        /// To start an instance, call <see cref="StartMod"/>
        /// </summary>
        /// <exception cref="ArgumentException">Mod with specified ID is already loaded.</exception>
        /// <param name="tuple">A tuple of mod config and path to config.</param>
        private ModInstance GetModInstance(PathGenericTuple<IModConfig> tuple)
        {
            // Check if mod with ID already loaded.
            if (IsModLoaded(tuple.Object.ModId))
                throw new ReloadedException(Errors.ModAlreadyLoaded(tuple.Object.ModId));

            // Load DLL or non-dll mod.
            if (File.Exists(tuple.Object.GetDllPath(tuple.Path)))
                return tuple.Object.IsNativeMod(tuple.Path) ? PrepareNativeMod(tuple) : PrepareDllMod(tuple);
            
            return PrepareNonDllMod(tuple);
        }

        private ModInstance PrepareDllMod(PathGenericTuple<IModConfig> tuple)
        {
            var modId = tuple.Object.ModId;
            var dllPath = tuple.Object.GetDllPath(tuple.Path);
            _modIdToFolder[modId] = Path.GetFullPath(Path.GetDirectoryName(tuple.Path));

            var loadContext     = LoadContext.BuildModLoadContext(dllPath, _modIdToMetadata[modId].IsUnloadable, GetExportsForModConfig(tuple.Object), _sharedContext.Context);
            var defaultAssembly = loadContext.LoadDefaultAssembly();
            var types           = defaultAssembly.GetTypes();
            var entryPoint      = types.FirstOrDefault(t => typeof(IModV1).IsAssignableFrom(t) && !t.IsAbstract);

            // Load entrypoint.
            var plugin = (IModV1) Activator.CreateInstance(entryPoint);
            return new ModInstance(loadContext, plugin, tuple.Object);
        }

        private ModInstance PrepareNativeMod(PathGenericTuple<IModConfig> tuple)
        {
            var modId = tuple.Object.ModId;
            var dllPath = tuple.Object.GetNativeDllPath(tuple.Path);
            _modIdToFolder[modId] = Path.GetFullPath(Path.GetDirectoryName(tuple.Path));
            return new ModInstance(new NativeMod(dllPath), tuple.Object);
        }

        private ModInstance PrepareNonDllMod(PathGenericTuple<IModConfig> tuple)
        {
            // If invalid file path, get directory.
            // If directory, use directory.
            if (Directory.Exists(tuple.Path))
                _modIdToFolder[tuple.Object.ModId] = Path.GetFullPath(tuple.Path);
            else
                _modIdToFolder[tuple.Object.ModId] = Path.GetFullPath(Path.GetDirectoryName(tuple.Path));

            return new ModInstance(tuple.Object);
        }

        private void StartMod(ModInstance instance)
        {
            LoaderApi.ModLoading(instance.Mod, instance.ModConfig);
            instance.Start(LoaderApi);
            _modifications[instance.ModConfig.ModId] = instance;
            LoaderApi.ModLoaded(instance.Mod, instance.ModConfig);
        }

        /* Setup for mod loading */
        private Type[] GetExportsForModConfig(IModConfig modConfig)
        {
            var exports = SharedTypes.AsEnumerable();

            // Share the mod's types with the mod itself.
            // The type is already preloaded into the default load context, and as such, will be inherited from the default context.
            // i.e. The version loaded into the default context will be used.
            // This is important because we need a single source for the other mods, i.e. ones which take this one as dependency.
            if (_modIdToMetadata.ContainsKey(modConfig.ModId))
                exports = exports.Concat(_modIdToMetadata[modConfig.ModId].Exports);

            foreach (var dep in modConfig.ModDependencies)
            {
                if (_modIdToMetadata.ContainsKey(dep))
                    exports = exports.Concat(_modIdToMetadata[dep].Exports);
            }

            foreach (var optionalDep in modConfig.OptionalDependencies)
            {
                if (_modIdToMetadata.ContainsKey(optionalDep))
                    exports = exports.Concat(_modIdToMetadata[optionalDep].Exports);
            }

            return exports.ToArray();
        }

        private void PreloadAssemblyMetadata(List<PathGenericTuple<IModConfig>> configPathTuples) => ExecuteWithStopwatch("Loading Assembly Metadata for Inter Mod Communication, Determining Unload Support etc.", PreloadAssemblyMetadataParallel, configPathTuples);
        private void PreloadAssemblyMetadataParallel(List<PathGenericTuple<IModConfig>> configPathTuples)
        {
            var partitioner = Partitioner.Create(0, configPathTuples.Count);
            Parallel.ForEach(partitioner, (tuple, state) =>
            {
                for (int x = tuple.Item1; x < tuple.Item2; x++)
                {
                    var configPathTuple = configPathTuples[x];
                    var dllPath = configPathTuple.Object.GetDllPath(configPathTuple.Path);
                    if (!File.Exists(dllPath) || configPathTuple.Object.IsNativeMod(configPathTuple.Path))
                        continue;

                    if (GetMetadataForDllMod(dllPath, out var exports, out bool isUnloadable))
                        _modIdToMetadata[configPathTuple.Object.ModId] = new ModAssemblyMetadata(exports, isUnloadable);
                }
            });
            
        }

        private bool GetMetadataForDllMod(string dllPath, out Type[] exports, out bool isUnloadable)
        {
            exports      = DefaultExportedTypes; // Preventing heap allocation here.
            isUnloadable = false;

            var loadContext     = LoadContext.BuildModLoadContext(dllPath, true, SharedTypes, _sharedContext.Context);
            var defaultAssembly = loadContext.LoadDefaultAssembly();
            var types           = defaultAssembly.GetTypes();

            var exportsEntryPoint = types.FirstOrDefault(t => typeof(IExports).IsAssignableFrom(t) && !t.IsAbstract);
            if (exportsEntryPoint != null)
            {
                var pluginExports = (IExports) Activator.CreateInstance(exportsEntryPoint);
                var exportedTypes = pluginExports.GetTypes();
                var assemblies    = LoadTypesIntoSharedContext(exportedTypes);
                exports           = new Type[exportedTypes.Length];

                // Find exports in assemblies that were just loaded into the default ALC.
                // If we don't do this; the assemblies will stay loaded in the other ALC because we are still holding a reference to them.
                var assemblyToTypes = new Dictionary<Assembly, Type[]>();
                foreach (var asm in assemblies)
                {
                    if (!assemblyToTypes.ContainsKey(asm)) 
                        assemblyToTypes[asm] = asm.GetTypes();
                }

                for (int x = 0; x < assemblies.Length; x++)
                {
                    var target        = exportedTypes[x];
                    var assemblyTypes = assemblyToTypes[assemblies[x]];
                    exports[x]        = assemblyTypes.First(y => y.FullName == target.FullName);
                }
            }

            var modEntryPoint = types.FirstOrDefault(t => typeof(IModV1).IsAssignableFrom(t) && !t.IsAbstract);
            if (modEntryPoint != null)
            {
                var plugin = (IModV1) Activator.CreateInstance(modEntryPoint);
                isUnloadable = plugin.CanUnload();
            }

            loadContext.Dispose();
            return true;
        }

        private Assembly[] LoadTypesIntoSharedContext(IReadOnlyList<Type> types)
        {
            var assemblies = new Assembly[types.Count];
            for (var x = 0; x < types.Count; x++)
            {
                var path = new Uri(types[x].Module.Assembly.CodeBase).LocalPath;
                assemblies[x] = _sharedContext.Context.LoadFromAssemblyPath(path);
            }

            return assemblies;
        }

        /* Utility */
        private void WriteLineAsync(string message)
        {
            _loader?.Console?.WriteLineAsync(AddLogPrefix(message));
        }

        private void ExecuteWithStopwatch<T>(string message, Action<T> code, T parameter)
        {
            var _stopwatch = new Stopwatch();
            _stopwatch.Start();
            code(parameter);
            WriteLineAsync($"{message}: Complete {_stopwatch.ElapsedMilliseconds}ms");
        }

        private Y ExecuteWithStopwatch<T, Y>(string message, Func<T, Y> code, T parameter)
        {
            var _stopwatch = new Stopwatch();
            _stopwatch.Start();
            var result = code(parameter);
            WriteLineAsync($"{message}: Complete {_stopwatch.ElapsedMilliseconds}ms");
            return result;
        }
    }
}