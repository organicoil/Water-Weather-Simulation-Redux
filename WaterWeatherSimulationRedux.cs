using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace WaterWeatherSimulationRedux;

public class ModConfig
{

    public float FreezingTemperature { get; set; } = -4;
    public float MeltingTemperature { get; set; } = 4;

    public string MeasurementType { get; set; } = "daily_average";
    public double AverageIntervalHours { get; set; } = 3;
    public double SpecificHour { get; set; } = 9;

    public bool FixSnowAccum { get; set; } = true;
    public bool RespectSnowAccum { get; set; } = true;

    public bool OnChunkLoad { get; set; } = true;
    public bool PeriodicUpdates { get; set; } = true;
    public int PeriodicUpdatesIntervalMillis { get; set; } = 60000; // 1 minute

}

//TODO: Cache temperature per chunk (to reduce load)
//TODO: Add snow on top of ice (if sufficient rainfall)
//TODO: (*) Handle waterlogged blocks
public class WaterWeatherSimulationRedux : ModSystem
{

    private const string CONFIG_FILE_NAME = "water_weather_simulation_redux.json";

    private const string ICE_BLOCK_PATH = "lakeice";
    private const string WATER_BLOCK_PATH = "water-still-7";

    private const int CHUNK_SIZE = 32;

    private ILogger LOG;

    private IWorldAccessor worldAccessor;
    private IWorldManagerAPI worldManager;
    private IBulkBlockAccessor bulkBlockAccessor;
    private IBlockAccessor runtimeBlockAccessor;

    private ModConfig config;

    private int iceBlockId;
    private int waterBlockId;

    //-------- Config/Initialization --------

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void AssetsFinalize(ICoreAPI api)
    {
        try
        {
            if (api is not ICoreServerAPI sapi)
            {
                api.Logger.Error("[WWSR] Failed to pre-initialize (failed to cast api to ICoreServerAPI)");
                return;
            }

            LOG = api.Logger;
            LOG.Debug("[WWSR] Starting pre-initialization...");

            worldAccessor = api.World;
            worldManager = sapi.WorldManager;
            bulkBlockAccessor = api.World.GetBlockAccessorMapChunkLoading(false);
            runtimeBlockAccessor = api.World.BlockAccessor;

            InitBlocks(api);

            LoadConfig(api);
            FixSnowAccumConfig(api);

            LOG.Debug("[WWSR] Pre-initialization complete");
        }
        catch (Exception e)
        {
            api.Logger.Error("[WWSR] Failed to pre-initialize");
            api.Logger.Error(e);
        }
    }

    private void InitBlocks(ICoreAPI api)
    {
        Block iceBlock = api.World.GetBlock(AssetLocation.Create(ICE_BLOCK_PATH));
        Block waterBlock = api.World.GetBlock(AssetLocation.Create(WATER_BLOCK_PATH));
        if (iceBlock == null)
        {
            throw new Exception($"[WWSR] Ice block not found ('{ICE_BLOCK_PATH}')");
        }
        if (waterBlock == null)
        {
            throw new Exception($"[WWSR] Water block not found ('{WATER_BLOCK_PATH}')");
        }
        iceBlockId = iceBlock.BlockId;
        waterBlockId = waterBlock.BlockId;
    }

    private void LoadConfig(ICoreAPI api)
    {
        LOG.Debug($"[WWSR] Starting to load config ('{CONFIG_FILE_NAME}')...");

        try
        {
            config = api.LoadModConfig<ModConfig>(CONFIG_FILE_NAME);
        }
        catch
        {
            config = null;
        }

        if (config == null)
        {
            LOG.Debug($"[WWSR] Config file not found or is invalid ('{CONFIG_FILE_NAME}'), a default config will be used");
            config = new ModConfig();
        }

        // Always save to file so that newly added properties are added to the file
        api.StoreModConfig(config, CONFIG_FILE_NAME);

        LOG.Debug($"[WWSR] Config loaded and saved to file ('{CONFIG_FILE_NAME}')");
    }

    // The base game has a bug with "snowAccum" config being stored as a string instead of a boolean
    // At the same time it is accessed as a boolean - which fails and default value (true) is always used
    private void FixSnowAccumConfig(ICoreAPI api)
    {
        if (!config.FixSnowAccum)
        {
            LOG.Debug("[WWSR] Skipping snowAccum config fix (FixSnowAccum config is disabled)");
            return;
        }

        LOG.Debug("[WWSR] Starting snowAccum config fix...");

        bool? snowAccumConfig = api.World.Config.TryGetBool("snowAccum");
        bool snowAccum;
        if (snowAccumConfig == null)
        {
            string snowAccumStr = api.World.Config.GetString("snowAccum");
            snowAccum = snowAccumStr?.ToLower().Equals("true") ?? false; // This seems more in line with how bool configs are handled by the game then bool.TryParse

            LOG.Debug($"[WWSR] Setting snowAccum config to '{snowAccum}' (as boolean)");
            api.World.Config.SetBool("snowAccum", snowAccum);
        }
        else
        {
            LOG.Debug($"[WWSR] snowAccum is already saved as boolean '{api.World.Config.TryGetBool("snowAccum")}'");
            snowAccum = snowAccumConfig.Value;
        }

        GlobalConstants.MeltingFreezingEnabled = snowAccum;

        LOG.Debug("[WWSR] snowAccum config fix applied");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        try
        {
            LOG.Debug("[WWSR] Starting initialization...");

            RegisterCommands(api);

            ConfigureWeatherSimulationSnowAccum(api);

            if (config.RespectSnowAccum && !GlobalConstants.MeltingFreezingEnabled)
            {
                LOG.Debug("[WWSR] Skipping event subscriptions due to disabled snowAccum config");
                return;
            }

            if (config.OnChunkLoad)
            {
                LOG.Debug("[WWSR] Registering OnChunkLoad event...");
                api.Event.BeginChunkColumnLoadChunkThread += OnChunkColumnLoad;
            }
            if (config.PeriodicUpdates)
            {
                LOG.Debug("[WWSR] Registering PeriodicUpdates event...");
                api.Event.RegisterGameTickListener(ProcessExistingChunks, config.PeriodicUpdatesIntervalMillis, config.PeriodicUpdatesIntervalMillis);
            }

            LOG.Debug("[WWSR] Initialization complete");
        }
        catch (Exception e)
        {
            api.Logger.Error("[WWSR] Failed to initialize");
            api.Logger.Error(e);
        }
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
           .Create("wwsrconfig")
           .WithAlias("wwsrc")
           .RequiresPrivilege(Privilege.controlserver)
           .HandleWith(_ => TextCommandResult.Success(GetConfigInfo()));

        api.ChatCommands
           .Create("wwsrdebug")
           .WithAlias("wwsrd")
           .RequiresPrivilege(Privilege.controlserver)
           .HandleWith(_ => TextCommandResult.Success(GetDebugInfo(api)));
    }

    private string GetConfigInfo()
    {
        string message = $"config.FreezingTemperature = {config.FreezingTemperature}"
                         + $"\nconfig.MeltingTemperature = {config.MeltingTemperature}"
                         + $"\nconfig.MeasurementType = {config.MeasurementType}"
                         + $"\nconfig.AverageIntervalHours = {config.AverageIntervalHours}"
                         + $"\nconfig.SpecificHour = {config.SpecificHour}"
                         + $"\nconfig.FixSnowAccum = {config.FixSnowAccum}"
                         + $"\nconfig.RespectSnowAccum = {config.RespectSnowAccum}";
        LOG.Debug($"[WWSR] GetConfigInfo: {message.Replace("\n", " | ")}");
        return message;
    }

    private string GetDebugInfo(ICoreServerAPI api)
    {
        WeatherSimulationSnowAccum snowAccumSystem = GetWeatherSimulationSnowAccumInstance(api);
        string message = $"snowAccumSystem.enabled = {snowAccumSystem.enabled}"
                         + $"\nsnowAccumSystem.ProcessChunks = {snowAccumSystem.ProcessChunks}"
                         + $"\nGlobalConstants.MeltingFreezingEnabled = {GlobalConstants.MeltingFreezingEnabled}"
                         + $"\nsnowAccum = {api.World.Config.TryGetBool("snowAccum")}";
        LOG.Debug($"[WWSR] GetDebugInfo: {message.Replace("\n", " | ")}");
        return message;
    }

    // WeatherSimulationSnowAccum.ProcessChunks is not affected by "snowAccum" config in the base game, even though it is used to add/remove snow during chunk updates
    private void ConfigureWeatherSimulationSnowAccum(ICoreServerAPI api)
    {
        if (!config.FixSnowAccum)
        {
            LOG.Debug("[WWSR] Skipping snowAccumSystem configuration (FixSnowAccum config is disabled)");
            return;
        }

        LOG.Debug("[WWSR] Starting configuring snowAccumSystem...");

        WeatherSimulationSnowAccum snowAccumSystem = GetWeatherSimulationSnowAccumInstance(api);
        if (snowAccumSystem != null)
        {
            snowAccumSystem.ProcessChunks = GlobalConstants.MeltingFreezingEnabled;

            LOG.Debug("[WWSR] Finished configuring snowAccumSystem");
        }
        else
        {
            LOG.Error("[WWSR] Failed to configure snowAccumSystem (WeatherSimulationSnowAccum not found)");
        }
    }

    // Using reflection as there doesn't seem to be a "proper" way to access WeatherSimulationSnowAccum instance
    private WeatherSimulationSnowAccum GetWeatherSimulationSnowAccumInstance(ICoreAPI api)
    {
        WeatherSystemServer wss = api.ModLoader.GetModSystem<WeatherSystemServer>();
        object snowAccumSystem = typeof(WeatherSystemServer).GetField("snowSimSnowAccu", BindingFlags.NonPublic | BindingFlags.Instance)
                                                           ?.GetValue(wss);
        return (WeatherSimulationSnowAccum) snowAccumSystem;
    }

    //-------- Events --------

    private void OnChunkColumnLoad(IServerMapChunk mapChunk, int chunkX, int chunkZ, IWorldChunk[] chunks)
    {
        try
        {
            bulkBlockAccessor.SetChunks(new Vec2i(chunkX, chunkZ), chunks);

            ProcessChunkBlocks(chunkX * CHUNK_SIZE, chunkZ * CHUNK_SIZE, bulkBlockAccessor);

            bulkBlockAccessor.Commit();
        }
        catch (Exception e)
        {
            LOG.Error($"[WWSR] Failed to process chunk (chunkX: {chunkX}, chunkZ: {chunkZ})");
            LOG.Error(e);
        }
    }

    private void ProcessExistingChunks(float dt)
    {
        try
        {
            foreach (KeyValuePair<long, IMapChunk> loadedChunkCopy in worldManager.AllLoadedMapchunks)
            {
                long index2d = loadedChunkCopy.Key;
                Vec2i chunkPos = worldManager.MapChunkPosFromChunkIndex2D(index2d);

                //TODO: (?) Queue only and then update random parts separately
                //TODO: (?) If no updates are made during iteration - skip next several iterations (to avoid unnecessary actions)
                ProcessChunkBlocks(chunkPos.X * CHUNK_SIZE, chunkPos.Y * CHUNK_SIZE, runtimeBlockAccessor);
            }
        }
        catch (Exception e)
        {
            LOG.Error("[WWSR] Failed to process existing chunks");
            LOG.Error(e);
        }
    }

    private void ProcessChunkBlocks(int chunkX, int chunkZ, IBlockAccessor blockAccessor)
    {
        BlockPos blockPos = new BlockPos(0, 0, 0, 0);
        for (int x = chunkX; x < chunkX + CHUNK_SIZE; x++)
        {
            for (int z = chunkZ; z < chunkZ + CHUNK_SIZE; z++)
            {
                try
                {
                    UpdateBlockPos(blockPos, x, z, blockAccessor);
                    ProcessBlock(blockPos, blockAccessor);
                }
                catch (Exception e)
                {
                    LOG.Error($"[WWSR] Failed to process block (x: {x}, z: {z})");
                    LOG.Error(e);
                }
            }
        }
    }

    private void UpdateBlockPos(BlockPos blockPos, int x, int z, IBlockAccessor blockAccessor)
    {
        blockPos.X = x;
        blockPos.Z = z;

        // Rain map height may return value out of world.
        int y = blockAccessor.GetRainMapHeightAt(blockPos);
        blockPos.Y = y < blockAccessor.MapSizeY ? y : worldAccessor.SeaLevel - 1;
    }

    private void ProcessBlock(BlockPos blockPos, IBlockAccessor blockAccessor)
    {
        Block block = blockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
        if (block.BlockId != waterBlockId && block.BlockId != iceBlockId)
        {
            return;
        }

        float temperature = getTemperature(blockPos);
        if (block.BlockId == waterBlockId && temperature < config.FreezingTemperature)
        {
            blockAccessor.SetBlock(iceBlockId, blockPos, BlockLayersAccess.Fluid);
        }
        else if (block.BlockId == iceBlockId && temperature > config.MeltingTemperature)
        {
            blockAccessor.SetBlock(waterBlockId, blockPos, BlockLayersAccess.Fluid);
        }
    }

    private float getTemperature(BlockPos blockPos)
    {
        return config.MeasurementType switch
               {
                   "now" => GetCurrentTemperature(blockPos),
                   "specific_hour" => GetSpecificHourTemperature(blockPos),
                   _ => GetDailyAverageTemperature(blockPos)
               };
    }

    private float GetCurrentTemperature(BlockPos blockPos)
    {
        return bulkBlockAccessor.GetClimateAt(blockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, worldAccessor.Calendar.TotalDays).Temperature;
    }

    private float GetSpecificHourTemperature(BlockPos blockPos)
    {
        int day = GetCurrentDay();
        double time = GetTime(config.SpecificHour);

        return GetSpecificHourTemperature(blockPos, day, time);
    }

    private float GetDailyAverageTemperature(BlockPos blockPos)
    {
        int day = GetCurrentDay();

        return GetDailyAverageTemperature(blockPos, day);
    }

    private float GetDailyAverageTemperature(BlockPos blockPos, int day)
    {
        double interval = Math.Min(config.AverageIntervalHours, 12);
        int intervalCount = (int) (24 / interval);

        float[] temperatureValues = new float[intervalCount];
        for (int i = 0; i < intervalCount; i++)
        {
            double time = GetTime(interval * i);
            temperatureValues[i] = GetSpecificHourTemperature(blockPos, day, time);
        }

        return temperatureValues.Average();
    }

    private float GetSpecificHourTemperature(BlockPos blockPos, int day, double time)
    {
        return bulkBlockAccessor.GetClimateAt(blockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, day + time).Temperature;
    }

    private int GetCurrentDay()
    {
        return (int) worldAccessor.Calendar.TotalDays;
    }

    private double GetTime(double hours)
    {
        return Math.Clamp(hours, 0, 23.9) / 24;
    }

}
