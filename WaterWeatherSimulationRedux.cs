using System;
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
    public double SpecificHourFreeze { get; set; } = 16;
    public double SpecificHourMelt { get; set; } = 4;

    public bool FixSnowAccum { get; set; } = true;
    public bool RespectSnowAccum { get; set; } = true;

}

//TODO: Add compatibility with `Config lib` (https://mods.vintagestory.at/configlib)
//TODO: Add snow on top of ice (if sufficient rainfall)
//TODO: (*) Add periodic updates outside of chunk load
//TODO: (*) Handle waterlogged blocks
public class WaterWeatherSimulationRedux : ModSystem
{

    private const string CONFIG_FILE_NAME = "water_weather_simulation_redux.json";

    private const string ICE_BLOCK_PATH = "lakeice";
    private const string WATER_BLOCK_PATH = "water-still-7";

    private ILogger LOG;

    private IWorldAccessor world;
    private IBulkBlockAccessor bulkBlockAccessor;

    private ModConfig config;

    private int iceBlockId;
    private int waterBlockId;

    //-------- Config/Initialization --------

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void AssetsFinalize(ICoreAPI api)
    {
        try
        {
            LOG = api.Logger;
            LOG.Debug("[WWSR] Starting pre-initialization...");

            world = api.World;
            bulkBlockAccessor = api.World.GetBlockAccessorMapChunkLoading(false);

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

            api.Event.BeginChunkColumnLoadChunkThread += EventOnBeginChunkColumnLoadChunkThread;

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
                         + $"\nconfig.SpecificHourFreeze = {config.SpecificHourFreeze}"
                         + $"\nconfig.SpecificHourMelt = {config.SpecificHourMelt}"
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

    private void EventOnBeginChunkColumnLoadChunkThread(IServerMapChunk mapChunk, int chunkX, int chunkZ, IWorldChunk[] chunks)
    {
        try
        {
            bulkBlockAccessor.SetChunks(new Vec2i(chunkX, chunkZ), chunks);

            ProcessChunkBlocks(chunkX, chunkZ);

            bulkBlockAccessor.Commit();
        }
        catch (Exception e)
        {
            LOG.Error($"[WWSR] Failed to process chunk (chunkX: {chunkX}, chunkZ: {chunkZ})");
            LOG.Error(e);
        }
    }

    private void ProcessChunkBlocks(int chunkX, int chunkZ)
    {
        BlockPos blockPos = new BlockPos(0, 0, 0, 0);
        for (int x = chunkX * 32; x < chunkX * 32 + 32; x++)
        {
            for (int z = chunkZ * 32; z < chunkZ * 32 + 32; z++)
            {
                try
                {
                    UpdateBlockPos(blockPos, x, z);
                    ProcessBlock(blockPos);
                }
                catch (Exception e)
                {
                    LOG.Error($"[WWSR] Failed to process block (x: {x}, z: {z})");
                    LOG.Error(e);
                }
            }
        }
    }

    private void UpdateBlockPos(BlockPos blockPos, int x, int z)
    {
        blockPos.X = x;
        blockPos.Z = z;

        // Rain map height may return value out of world.
        int y = bulkBlockAccessor.GetRainMapHeightAt(blockPos);
        blockPos.Y = y < bulkBlockAccessor.MapSizeY ? y : world.SeaLevel - 1;
    }

    private void ProcessBlock(BlockPos blockPos)
    {
        Block block = bulkBlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
        if (block.BlockId != waterBlockId && block.BlockId != iceBlockId)
        {
            return;
        }

        float temperature = getTemperature(blockPos, block);
        if (temperature < config.FreezingTemperature)
        {
            bulkBlockAccessor.SetBlock(iceBlockId, blockPos, BlockLayersAccess.Fluid);
        }
        else if (temperature > config.MeltingTemperature)
        {
            bulkBlockAccessor.SetBlock(waterBlockId, blockPos, BlockLayersAccess.Fluid);
        }
    }

    private float getTemperature(BlockPos blockPos, Block block)
    {
        return config.MeasurementType switch
               {
                   "now" => GetCurrentTemperature(blockPos),
                   "specific_hour" => GetSpecificHourTemperature(blockPos, block),
                   _ => GetDailyAverageTemperature(blockPos)
               };
    }

    private float GetCurrentTemperature(BlockPos blockPos)
    {
        return bulkBlockAccessor.GetClimateAt(blockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays).Temperature;
    }

    private float GetSpecificHourTemperature(BlockPos blockPos, Block block)
    {
        double hours = block.Id == waterBlockId ? config.SpecificHourFreeze : config.SpecificHourMelt;

        int currentDay = GetCurrentDay();
        int additionalDays = hours >= 0 ? (int) (hours / 24) : (int) ((hours - 24) / 24);
        int day = currentDay + additionalDays;

        double time = GetTime(hours);

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
        return (int) world.Calendar.TotalDays;
    }

    private double GetTime(double hours)
    {
        hours = hours switch
                {
                    > 24 => hours % 24,
                    < 0 => 24 + (hours % 24),
                    _ => hours
                };

        return hours / 24;
    }

}
