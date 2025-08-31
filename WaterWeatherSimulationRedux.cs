using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WaterWeatherSimulationRedux;

public class ModConfig
{

    public int FreezingTemperature { get; set; } = -4;
    public int MeltingTemperature { get; set; } = 4;

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
        LOG = api.Logger;

        LOG.Debug("[WWSR] Starting initialization...");

        world = api.World;
        bulkBlockAccessor = api.World.GetBlockAccessorMapChunkLoading(false);

        InitBlocks(api);

        LoadConfig(api);

        api.Event.BeginChunkColumnLoadChunkThread += EventOnBeginChunkColumnLoadChunkThread;

        LOG.Debug("[WWSR] Initialization complete");
    }

    private void InitBlocks(ICoreServerAPI api)
    {
        Block iceBlock = api.World.GetBlock(AssetLocation.Create("lakeice"));
        Block waterBlock = api.World.GetBlock(AssetLocation.Create("water-still-7"));
        if (iceBlock == null)
        {
            throw new Exception("[WWSR] Ice block not found by id ('lakeice')");
        }
        if (waterBlock == null)
        {
            throw new Exception("[WWSR] Water block not found by id ('water-still-7')");
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
        bulkBlockAccessor.SetChunks(new Vec2i(chunkX, chunkZ), chunks);

        BlockPos blockPos = new BlockPos(0, 0, 0, 0);
        for (int x = chunkX * 32; x < chunkX * 32 + 32; x++)
        {
            for (int z = chunkZ * 32; z < chunkZ * 32 + 32; z++)
            {
                blockPos.X = x;
                blockPos.Z = z;

                // Rain map height may return value out of world.
                int y = bulkBlockAccessor.GetRainMapHeightAt(blockPos);
                blockPos.Y = y < bulkBlockAccessor.MapSizeY ? y : world.SeaLevel - 1;

                Block block = bulkBlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
                if (block.BlockId != waterBlockId && block.BlockId != iceBlockId)
                {
                    continue;
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
        }

        bulkBlockAccessor.Commit();
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
