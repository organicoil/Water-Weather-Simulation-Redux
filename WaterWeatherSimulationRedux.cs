using JetBrains.Annotations;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WaterWeatherSimulationRedux;

public class ModConfig
{

    public int FreezingTemperature { get; set; } = -10;
    public int MeltingTemperature { get; set; } = 10;

}

//TODO: Improve temperature reading to make it time-of-day agnostic (daily average / general climate / temperature for specific time of day)
//TODO: Handle waterlogged blokes
[UsedImplicitly]
public class WaterWeatherSimulationRedux : ModSystem
{

    private IWorldAccessor world;
    private IBulkBlockAccessor bulkBlockAccessor;

    private ModConfig config;

    private int iceBlockId;
    private int waterBlockId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        LoadConfig(api);

        Block iceBlock = api.World.GetBlock(AssetLocation.Create("lakeice"));
        Block waterBlock = api.World.GetBlock(AssetLocation.Create("water-still-7"));
        if (iceBlock == null || waterBlock == null)
        {
            return;
        }

        iceBlockId = iceBlock.BlockId;
        waterBlockId = waterBlock.BlockId;
        world = api.World;
        bulkBlockAccessor = api.World.GetBlockAccessorMapChunkLoading(false);
        api.Event.BeginChunkColumnLoadChunkThread += EventOnBeginChunkColumnLoadChunkThread;
    }

    private void LoadConfig(ICoreServerAPI api)
    {
        try
        {
            config = api.LoadModConfig<ModConfig>("water_weather_tweaks.json");
        }
        catch
        {
            config = null;
        }
        if (config == null)
        {
            config = new ModConfig();
            api.StoreModConfig(config, "water_weather_tweaks.json");
        }
    }

    private void EventOnBeginChunkColumnLoadChunkThread(IServerMapChunk mapChunk, int chunkX, int chunkZ, IWorldChunk[] chunks)
    {
        bulkBlockAccessor.SetChunks(new Vec2i(chunkX, chunkZ), chunks);

        BlockPos blockPos = new BlockPos(0, 0, 0, 0);
        for (int x = chunkX * 32; x < chunkX * 32 + 32; x += 1)
        {
            for (int z = chunkZ * 32; z < chunkZ * 32 + 32; z += 1)
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

                float temp = bulkBlockAccessor.GetClimateAt(blockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays).Temperature;
                if (temp < config.FreezingTemperature)
                {
                    bulkBlockAccessor.SetBlock(iceBlockId, blockPos, BlockLayersAccess.Fluid);
                }
                else if (temp > config.MeltingTemperature)
                {
                    bulkBlockAccessor.SetBlock(waterBlockId, blockPos, BlockLayersAccess.Fluid);
                }
            }
        }

        bulkBlockAccessor.Commit();
    }

}