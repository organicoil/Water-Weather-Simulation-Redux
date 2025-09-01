using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WaterWeatherSimulationRedux;

public class ModConfig
{

    public float FreezingTemperature { get; set; } = -4;
    public float MeltingTemperature { get; set; } = 4;

    public string MeasurementType { get; set; } = "daily_average";
    public double AverageIntervalHours { get; set; } = 3;
    public double SpecificHour { get; set; } = 9;

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

    public override void StartServerSide(ICoreServerAPI api)
    {
        try
        {
            api.Logger.Debug("[WWSR] Starting initialization...");

            LOG = api.Logger;
            world = api.World;
            bulkBlockAccessor = api.World.GetBlockAccessorMapChunkLoading(false);

            InitBlocks(api);
            LoadConfig(api);

            api.Event.BeginChunkColumnLoadChunkThread += EventOnBeginChunkColumnLoadChunkThread;

            api.Logger.Debug("[WWSR] Initialization complete");
        }
        catch (Exception e)
        {
            api.Logger.Error("[WWSR] Failed to initialize");
            api.Logger.Error(e);
        }
    }

    private void InitBlocks(ICoreServerAPI api)
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

    private void LoadConfig(ICoreServerAPI api)
    {
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
            LOG.Debug($"[WWSR] Config file not found or is invalid ('{CONFIG_FILE_NAME}'), creating a default config file...");

            config = new ModConfig();
            api.StoreModConfig(config, CONFIG_FILE_NAME);

            LOG.Debug($"[WWSR] Default config file created ('{CONFIG_FILE_NAME}')");
        }
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

        float temperature = getTemperature(blockPos);
        if (temperature < config.FreezingTemperature)
        {
            bulkBlockAccessor.SetBlock(iceBlockId, blockPos, BlockLayersAccess.Fluid);
        }
        else if (temperature > config.MeltingTemperature)
        {
            bulkBlockAccessor.SetBlock(waterBlockId, blockPos, BlockLayersAccess.Fluid);
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
        return bulkBlockAccessor.GetClimateAt(blockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays).Temperature;
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
        return (int) world.Calendar.TotalDays;
    }

    private double GetTime(double hours)
    {
        return Math.Min(hours, 23.9) / 24;
    }

}
