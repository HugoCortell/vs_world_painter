using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Config; // for GamePaths.ModConfig
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.CommandAbbr;
using System.Globalization;

#region Design Decisions
// Heightmap:
// We use a granite "rock buffer" to impose our heightmap upon terragen, which terragen then overwrites with strata and other stuff.
// The reason for this is because it avoids patching, which means less work to maintain, and it works well enough.

// Metadata Layers:
// We translate custom layers from world painter into data that we use to overwrite/supplement the default generator's maps.
// Look at the export script for detail on how that works.
// We intercept ServerWorldMap.getWorldGenClimateAt(pos, ...) and OnMapRegionGen. We also overwrite/modify mapRegion.ClimateMap.
// Layers are an optional bit of creative control, if a layer was unused and not included in our file, we simply use the vanilla map.
// We apply the metadata values raw, letting the game then adjustment based on esoteric criteria like height and other maps's weights.

// About Sea Level: Sea level is ~40% of world height. The default map height is of 256, so that's about 110 blocks.
// This means that the normal ground level for a map in world painter should be ~115 - ~150.
#endregion

namespace WorldPainter
{
	public class WorldPainterModSystem : ModSystem
	{
		ICoreServerAPI? ServerAPI;
		IWorldGenBlockAccessor? GenTerraWorldAccessor;
		HeightMap? WorldHeightmap;

		// Prevent mod from messing with previously existing saves
		const string SaveKeyWorldPainter = "WorldPainterSave";
		bool ModEnabledInWorld = false;

		#region Initialization & Safety
		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerAPI = api;

			/// ┌────────────────────────────────┐
			/// │ Save File Integrity Protection │
			/// └────────────────────────────────┘
			// Brand new worlds with a flag that it's safe to load into them with our mod
			ServerAPI.Event.SaveGameCreated += () =>
			{
				ServerAPI.WorldManager.SaveGame.StoreData(SaveKeyWorldPainter, true);
				ServerAPI.Logger.Event("[worldpainter] Save file marked as World Painter compatible.");
			};

			// Check for the flag on world load, kick the player out if the flag is missing
			// This prevents the player from unknowingly having the mod enabled and it damaging an existing save file
			ServerAPI.Event.SaveGameLoaded += () =>
			{
				ModEnabledInWorld = ServerAPI.WorldManager.SaveGame.GetData(SaveKeyWorldPainter, false);
				ServerAPI.Logger.Event($"[worldpainter] World Painter world flag {(ModEnabledInWorld ? "found." : "not found!")}");

				if (!ModEnabledInWorld) // STOP CODE EXECUTION AT ALL COSTS TO PROTECT THE INTEGRITY OF USER DATA. MARTYR THE PROGRAM IF NECESSARY.
				{
					ServerAPI.Logger.Event($"[worldpainter] [CRITICAL ERROR] Panic! Server will now crash to protect the save file from damage.");
					System.Environment.FailFast
					(
						"The World Painter mod is currently enabled in your game or server,\n" +
						"but the save file you are trying to load was not created using it.\n\n" +
						"Please remember to disable the World Painter mod before trying to load worlds made without it." + 
						"Were it not for this crash, your save file may have become corrupted. Please be careful."
					); // End-user won't actually get this message as a pop-up
				}

				// Allow clients to request pre-generation at will
				ServerAPI.ChatCommands
					.Create("paintworld").WithDescription("Pre-generate all map chunks to force them onto the save file")
					.RequiresPrivilege(Privilege.controlserver)
					.HandleWith(args =>
					{
						if (!ModEnabledInWorld || WorldHeightmap == null) { return TextCommandResult.Error("Refusing command, missing map data."); }

						ForceMapAreaGeneration((args.Caller.Player as IServerPlayer)!);
						return TextCommandResult.Success("Pre-generating world on request, please wait...");
					}
				);
			};


			/// ┌─────────────────────────┐ Programatically force the world to pre-generate,
			/// │ Active World Generation │ ensuring that all chunks have been written to before the user
			/// └─────────────────────────┘ potentialy logs out and disables the mod or does anything that disrupt world generation.
			ServerAPI.Event.PlayerJoin += (IServerPlayer player) =>
			{
				if (!ServerAPI.WorldManager.SaveGame.IsNew) { return; }

				if (player == null) { ForceMapAreaGeneration(player!); } // If we are a server, start forced chunk generation automatically
				else // Otherwise we let the user decide, in case this is a test world and they just want to fly around
				{ 
					ServerAPI.SendMessageToGroup
					(
						GlobalConstants.AllChatGroups,
						"Welcome to your World Painter map!\n" +
						"Remember that you can use /paintworld to pre-generate all map chunks.\n" +
						"Please refer to the included User Guide for more information.",
						EnumChatType.Notification
					);  // Todo: this may be too early in the execution order for a player to recieve?
				}
			};


			/// ┌────────────────┐
			/// │ File Discovery │ Now with support for arbitrarily named files!! Woah!
			/// └────────────────┘ Ensure ModConfig/worldpainter folder exists, we look for .wphm files there
			var intermediaryFileDirectory = Path.Combine(GamePaths.ModConfig, "worldpainter");
			try { Directory.CreateDirectory(intermediaryFileDirectory); } catch {}
			var wphmFiles = Directory.EnumerateFiles(intermediaryFileDirectory)
				.Where(fileExt => string.Equals(Path.GetExtension(fileExt), ".wphm", StringComparison.OrdinalIgnoreCase))
				.Take(2) // To detect if multiple files exist in folder
				.ToArray(); // WPHM stands for WorldPainterHeightMap, from back in the original implementation (tmyk)
			
			if (wphmFiles.Length == 0)
			{
				ServerAPI.Logger.Warning($"[worldpainter] WPHM file not found! Can't build a world without it! Expected at: {intermediaryFileDirectory}");
				return;
			}
			if (wphmFiles.Length > 1) { ServerAPI.Logger.Warning($"[worldpainter] Multiple map files found, there should only be one! Reading the first one."); }
			string resolvedPath = wphmFiles[0];
			#endregion

			#region Heightmap Handling
			try { WorldHeightmap = HeightMap.Load(resolvedPath); }
			catch (Exception error) { ServerAPI.Logger.Error($"[worldpainter] [CRITICAL ERROR] Failed to access heightmap! {error.Message}"); return; }

			// Center the import within world bounds
			var worldBlockAccessor = ServerAPI.World.BlockAccessor; // Should be safe to use for size queries
			int worldSizeX = worldBlockAccessor.MapSizeX, wsz = worldBlockAccessor.MapSizeZ;
			int wantOx = GameMath.Clamp((worldSizeX - WorldHeightmap.Width)  / 2, 0, Math.Max(0, worldSizeX - WorldHeightmap.Width));
			int wantOz = GameMath.Clamp((wsz - WorldHeightmap.Height) / 2, 0, Math.Max(0, wsz - WorldHeightmap.Height));
			int addOx = wantOx - WorldHeightmap.OriginX;
			int addOz = wantOz - WorldHeightmap.OriginZ;
			if (addOx != 0 || addOz != 0)
			{
				WorldHeightmap.ApplyOffset(addOx, addOz);
				ServerAPI.Logger.Event
				(
					$"[worldpainter] Centered World: World Size = ({worldSizeX}x{wsz}) | "
					+ $"New Origin = ({WorldHeightmap.OriginX}-{WorldHeightmap.OriginZ})"
				);
			}
			else
			{ 
				ServerAPI.Logger.Event
				(
					$"[worldpainter] Imported world already at {WorldHeightmap.OriginX}-{WorldHeightmap.OriginZ}"
					+ $"(World Size = ({worldSizeX}x{wsz})))"
				);
			}

			ServerAPI.Logger.Event
			(
				$"[worldpainter] Loaded {Path.GetFileName(resolvedPath)} | "
				+ $"World Size: {WorldHeightmap.Width}x{WorldHeightmap.Height}) | "
				+ $"Origin = {WorldHeightmap.OriginX}-{WorldHeightmap.OriginZ} | "
				+ $"Samples = {WorldHeightmap.SampleMin}-{WorldHeightmap.SampleMax}"
				+ $"Covereage Bounds = X({WorldHeightmap.OriginX}-{WorldHeightmap.OriginX + WorldHeightmap.Width - 1}) "
				+ $"Z({WorldHeightmap.OriginZ}-{WorldHeightmap.OriginZ + WorldHeightmap.Height - 1})"
			);
			if (WorldHeightmap.LayerNames != null)
			{
				var layrnames = string.Join(", ", WorldHeightmap.LayerNames);
				ServerAPI.Logger.Event($"[worldpainter] Metadata Layers: {layrnames}");
			}

			// Get worldgen-safe accessor once
			ServerAPI.Event.GetWorldgenBlockAccessor(provider => { GenTerraWorldAccessor = provider.GetBlockAccessor(true); });

			// Register & adjust handlers for standard world type
			ServerAPI.Event.InitWorldGenerator(() =>
			{
				// MapRegion - inject our paint rasters into the vanilla region maps
				ServerAPI.Event.MapRegionGeneration(OnMapRegion, "standard");
				ServerAPI.Event.MapChunkGeneration(OnMapChunk, "standard"); // Reveal our heights so vanilla stages can see the surface

				// Base terrain filler in the Terrain pass + ocean (fluids before block-layers)
				ServerAPI.Event.ChunkColumnGeneration(OnChunkColumnTerrain, EnumWorldGenPass.Terrain, "standard");
				ServerAPI.Event.ChunkColumnGeneration(OnSeaFill, EnumWorldGenPass.Terrain, "standard");

				// Remove (only) GenTerra's terrain handler, we keep strata, soils, deposits, caves, vegetation, lighting, etc
				var handlers = ServerAPI.Event.GetRegisteredWorldGenHandlers("standard");
				var terrainList = handlers?.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];
				if (terrainList != null)
				{
					int removed = terrainList.RemoveAll
					(
						d => d != null && d.Method != null &&
						(
							(d.Method.Name == "OnChunkColumnGen" && (d.Method.DeclaringType?.Name?.Contains("GenTerra") ?? false)) ||
							(d.Method.DeclaringType?.FullName?.Contains("GenTerra") ?? false)
						)
					);
					if (removed > 0) ServerAPI.Logger.Event($"[worldpainter] Removed GenTerra ({removed}).");

					// Run Terrain first so strata/layers see our stone world
					terrainList.Remove(OnChunkColumnTerrain); terrainList.Insert(0, OnChunkColumnTerrain);

					// Run SeaFill right after our stone fill so that layers see fluids too
					terrainList.Remove(OnSeaFill); terrainList.Insert(1, OnSeaFill);
				}

				// Ensure our MapChunk writer runs first as well
				var mapchunkList = handlers?.OnMapChunkGen;
				if (mapchunkList != null) { mapchunkList.Remove(OnMapChunk); mapchunkList.Insert(0, OnMapChunk); }

				// Add a pre-vegetation pass to refresh RainHeightMap after soils/layers
				ServerAPI.Event.ChunkColumnGeneration(OnPreVegetationRefreshRain, EnumWorldGenPass.Vegetation, "standard");
				var vegList = handlers?.OnChunkColumnGen[(int)EnumWorldGenPass.Vegetation];
				if (vegList != null) { vegList.Remove(OnPreVegetationRefreshRain); vegList.Insert(0, OnPreVegetationRefreshRain); }

				// Ensure our MapRegion writer runs *after* vanilla GenMaps
				var regList = handlers?.OnMapRegionGen;
				if (regList != null) { regList.Remove(OnMapRegion); regList.Add(OnMapRegion); }

				// Gate out all *non-lighting* handlers to stop the tectonic layer from spreading outside world bounds
				for (int passindex = 0; passindex < handlers?.OnChunkColumnGen.Length; passindex++)
				{
					var list = handlers.OnChunkColumnGen[passindex]; if (list == null) continue;
					if (passindex == (int)EnumWorldGenPass.PreDone) continue; // Let lighting run everywhere
					for (int i = 0; i < list.Count; i++)
					{
						var del = list[i];
						if (del == null) continue;
						if (del == OnChunkColumnTerrain || del == OnSeaFill || del == OnPreVegetationRefreshRain) continue; // Keep our own handlers unwrapped
						var orig = del;
						list[i] = (IChunkColumnGenerateRequest req) => { if (ChunkOverlapsMap(req.ChunkX, req.ChunkZ)) orig(req); }; // else skip entirely outside chunks
					} 
				}

				// Final clean-up pass at the end of Vegetation to wipe outside columns
				ServerAPI.Event.ChunkColumnGeneration(OnEraseOutsideColumns, EnumWorldGenPass.Vegetation, "standard");
				if (vegList != null) { vegList.Remove(OnEraseOutsideColumns); vegList.Add(OnEraseOutsideColumns); }

				// Perform a beach pass to handle & enhance beaches
				ServerAPI.Event.ChunkColumnGeneration(OnBeachBand, EnumWorldGenPass.Vegetation, "standard");
				if (vegList != null) { vegList.Remove(OnBeachBand); vegList.Add(OnBeachBand); }
			}, "standard");
		}
		#endregion

		#region World Gen
		void OnMapRegion(IMapRegion region, int regionX, int regionZ, ITreeAttribute chunkGenParams)
		{
			// Bother only with overwriting data inside our import area (Still fill padded edges by sampling world coords, if outside import, we skip.)
			if (WorldHeightmap == null) { ServerAPI?.Logger.Error($"[worldpainter] [CRITICAL ERROR] No WorldHeightmap on OnMapRegion!"); return; }

			// Oceanness: Overwrite base map with raw U8
			if (WorldHeightmap.HasLayer("water_ocean")) { OverwriteScalarMapFromLayer(region.OceanMap, "water_ocean", regionX, regionZ); }
			if (WorldHeightmap.HasLayer("water_beach")) { OverwriteScalarMapFromLayer(region.BeachMap, "water_beach", regionX, regionZ); }

			// Vegetation (hotspotting fallthrough support & adaptive distance-field blend falloff) | Flowers maps are unused in-game right now
			if (WorldHeightmap.HasLayer("vegetation_forest"))		{ ApplyHotspotScalar(region.ForestMap, "vegetation_forest", regionX, regionZ); }
			if (WorldHeightmap.HasLayer("vegetation_shrubbery"))	{ ApplyHotspotScalar(region.ShrubMap, "vegetation_shrubbery", regionX, regionZ); }
			if (WorldHeightmap.HasLayer("vegetation_flowers"))		{ ApplyHotspotScalar(region.FlowerMap, "vegetation_flowers", regionX, regionZ); }

			// Climate (per-channel fallthrough support & adaptive distance-field blend falloff) | Packed 0xTT RR GG (T, R, and G/tectonics too).
			if (region.ClimateMap != null)
			{
				if (WorldHeightmap.HasLayer("climate_temperature"))	ApplyHotspotClimateChannel(region.ClimateMap, regionX, regionZ, "climate_temperature", ch: 0);
				if (WorldHeightmap.HasLayer("climate_moisture"))	ApplyHotspotClimateChannel(region.ClimateMap, regionX, regionZ, "climate_moisture", ch: 1);
				if (WorldHeightmap.HasLayer("climate_tectonic"))	ApplyHotspotClimateChannel(region.ClimateMap, regionX, regionZ, "climate_tectonic", ch: 2);
			} // Ch 0 == T, Ch 1 == R, Ch 2 == G (low byte)

			// Ore spawn multiplier | 0-15, x0.25 to x4, neutral (1) at step 8 (50% brush strength).
			if (WorldHeightmap.HasLayer("ore_multiplier")) { ApplyOreMultiplier(region, regionX, regionZ); }
		}

		void OverwriteScalarMapFromLayer(IntDataMap2D target, string layerName, int regionX, int regionZ)
		{
			if (target == null) return;
			int size = target.Size;
			if (size <= 0) return;
			int topLeft = target.TopLeftPadding;
			int bottomRight = target.BottomRightPadding;
			int inner = size - topLeft - bottomRight;
			if (inner <= 0) return;

			int regionSize = ServerAPI!.WorldManager.RegionSize;
			var data = target.Data ?? new int[size * size];

			for (int iz = 0; iz < size; iz++)
			{
				float fz = ((iz - topLeft) + 0.5f) / inner; // pos inside the unpadded inner area in [0-1], allow for padding
				int wz = (int)Math.Floor(regionZ * (double)regionSize + fz * regionSize);
				for (int ix = 0; ix < size; ix++)
				{
					float fx = ((ix - topLeft) + 0.5f) / inner;
					int wx = (int)Math.Floor(regionX * (double)regionSize + fx * regionSize);

					// Only overwrite values where our raster exists, otherwise keep ("fall through to") vanilla data.
					// Write per padded-pixel using nearest neighbour from our raster. VS bilerp reads the inner area later, padding just stabilizes edge samples.
					if (WorldHeightmap!.ContainsWorld(wx, wz)) // Fixed mapping from 0-15 to 0-255
					{
						byte v = WorldHeightmap.SampleLayerU8Remapped(layerName, wx, wz);
						data[iz * size + ix] = v;
					}
				}
			}
			target.Data = data;
		}

		// Write climate pixels from rasters | 0xTT RR GG (T=temperature, R=rainfall/moisture, G=geologic activity), each 0-255.
		// Inputs are low bit-depth 0-15, we map to 0-255.
		void OverwriteClimateMapFromLayers(IntDataMap2D target, int regionX, int regionZ, bool useTemp, bool useMoisture, bool useTect)
		{
			if (target == null) return;
			int n = target.Size * target.Size;
			var data = target.Data ?? new int[n];
			ForEachRegionPixel(target, regionX, regionZ, (idx, wx, wz) =>
			{
				int cur = data[idx];
				DecodeTRG(cur, out int t, out int r, out int g);
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) return;
				if (useTemp)		t = WorldHeightmap.SampleLayerU8Remapped("climate_temperature", wx, wz);
				if (useMoisture)	r = WorldHeightmap.SampleLayerU8Remapped("climate_moisture",    wx, wz);
				if (useTect)		g = WorldHeightmap.SampleLayerU8Remapped("climate_tectonic",    wx, wz);
				data[idx] = EncodeTRG(t, r, g);
			});
			target.Data = data;
		}

		void OnMapChunk(IMapChunk mapChunk, int chunkX, int chunkZ)
		{
			var blockAccessor = GenTerraWorldAccessor; // worldgen-time accessor only
			int csize = GlobalConstants.ChunkSize;

			var heightMap	= mapChunk.WorldGenTerrainHeightMap;
			var rainMap		= mapChunk.RainHeightMap;
			int baseX		= chunkX * csize;
			int baseZ		= chunkZ * csize;
			ushort ymax		= 0;
			
			for (int lz = 0; lz < csize; lz++)
			for (int lx = 0; lx < csize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) continue;

				int y = WorldHeightmap.SampleToWorldY(ServerAPI!, wx, wz);
				if ((uint)y < (uint)blockAccessor!.MapSizeY)
				{
					int idx = lz * csize + lx;
					ushort uy = (ushort)y;
					heightMap[idx] = uy; // terrain height (pre-vegetation)
					rainMap[idx] = uy; // topmost solid for rain/skylight
					if (uy > ymax) ymax = uy;
				}
			}
 
			mapChunk.YMax = ymax;
			mapChunk.MarkDirty();
		}

		void OnChunkColumnTerrain(IChunkColumnGenerateRequest req)
		{
			var worldBlockAccessor = GenTerraWorldAccessor; // must exist here
			var blockAccessor = (IBlockAccessor)worldBlockAccessor!;
			int csize = GlobalConstants.ChunkSize;

			int stoneID = ServerAPI!.World.GetBlock(new AssetLocation("game:rock-granite")).BlockId; // Base rock for our buffer, terragen likes specifically granite
			int mantleID = ServerAPI.World.GetBlock(new AssetLocation("game:mantle")).BlockId; // To prevent players from falling off the world (RIP Nepentus)
			var pos = new BlockPos(Dimensions.NormalWorld);

			int baseX = req.ChunkX * csize;
			int baseZ = req.ChunkZ * csize;

			worldBlockAccessor!.BeginColumn();
			for (int lz = 0; lz < csize; lz++)
			for (int lx = 0; lx < csize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) continue; // Only touch columns inside our coverage!

				int h = WorldHeightmap.SampleToWorldY(ServerAPI, wx, wz);
				{ pos.Set(wx, 0, wz); blockAccessor.SetBlock(mantleID, pos); } // Always lay mantle at y=0
				if (h < 1) continue;

				// Base fill, provide a solid granite column that vanilla strata/layers can transform
				for (int y = 1; y <= h; y++) { pos.Set(wx, y, wz); blockAccessor.SetBlock(stoneID, pos); }
			}
		}

		// Column/Chunk vs map rectangle intersection
		bool ChunkOverlapsMap(int chunkX, int chunkZ)
		{
			int chunk = GlobalConstants.ChunkSize;
			int x0 = chunkX * chunk, z0 = chunkZ * chunk;
			int x1 = x0 + chunk - 1,   z1 = z0 + chunk - 1;
			int mx0 = WorldHeightmap!.OriginX, mz0 = WorldHeightmap.OriginZ;
			int mx1 = WorldHeightmap.OriginX + WorldHeightmap.Width  - 1;
			int mz1 = WorldHeightmap.OriginZ + WorldHeightmap.Height - 1;
			return x0 <= mx1 && x1 >= mx0 && z0 <= mz1 && z1 >= mz0;
		}

		// Hard clean-up for outside columns (late pass)
		void OnEraseOutsideColumns(IChunkColumnGenerateRequest req)
		{
			var blockAccessor	= (IBlockAccessor)GenTerraWorldAccessor!;
			int chunkSize		= GlobalConstants.ChunkSize;
			int mapY			= blockAccessor.MapSizeY;
			int baseX			= req.ChunkX * chunkSize;
			int baseZ			= req.ChunkZ * chunkSize;

			// Target mapchunk arrays to zero out surface metadata too
			var mapChunk		= GenTerraWorldAccessor!.GetMapChunk(req.ChunkX, req.ChunkZ);
			var rainMap			= mapChunk.RainHeightMap;
			var heightMap		= mapChunk.WorldGenTerrainHeightMap;

			var chunks = req.Chunks;
			bool anyTouched = false;

			// If the chunk is fully outside the import zone, skip entirely. Gating already prevents other handlers from running there.
			if (!ChunkOverlapsMap(req.ChunkX, req.ChunkZ)) return;

			GenTerraWorldAccessor.BeginColumn();
			for (int lz = 0; lz < chunkSize; lz++)
			for (int lx = 0; lx < chunkSize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				if (WorldHeightmap!.ContainsWorld(wx, wz)) continue; // keep inside intact
				
				for (int y = 0; y <= mapY - 2; y++) // Wipe column completely, no mantle outside the import
				{
					int cy = y / chunkSize, ly = y % chunkSize;
					if ((uint)cy >= (uint)chunks.Length || chunks[cy] == null) continue;
					int idx3d = (chunkSize * ly + lz) * chunkSize + lx;
					var data = chunks[cy].Data;
					if (data.GetBlockIdUnsafe(idx3d) != 0 || data.GetFluid(idx3d) != 0)
					{
						data.SetBlockUnsafe(idx3d, 0);
						data.SetFluid(idx3d, 0);
						chunks[cy].MarkModified();
						anyTouched = true;
					}
				}

				// Fix heightmaps to "0" (mantle-only column)
				int idx2d = lz * chunkSize + lx;
				if (heightMap[idx2d] != 0 || rainMap[idx2d] != 0)
				{
					heightMap[idx2d] = 0;
					rainMap[idx2d] = 0;
					anyTouched = true;
				}
			}

			if (anyTouched)
			{
				// Recompute YMax from current height/rain maps
				ushort ymax = 0;
				int count2d = chunkSize * chunkSize;
				for (int i = 0; i < count2d; i++)
				{
					ushort h = heightMap[i];
					ushort r = rainMap[i];
					if (h > ymax) ymax = h;
					if (r > ymax) ymax = r;
				}
				mapChunk.YMax = ymax;
				mapChunk.MarkDirty();
			}
		}
		#endregion

		#region Hotspotting & SDFs
		/// ┌─────────────────────┐
		/// │ Hotspotting Support │
		/// └─────────────────────┘ Settings & Helpers
		const int PaintThreshold = 1;
		const float VegetationInfluenceRadius	= 96f;
		const float ClimateInfluenceRadius		= 160f;

		// Calculate coverage ratio of painted pixels within the inner area (unpadded)
		float ComputeInnerCoverageRaw(IntDataMap2D target, string layerName, int regionX, int regionZ)
		{
			int painted = 0, inside = 0;
			ForEachRegionInnerPixel(target, regionX, regionZ, (idx, wx, wz) =>
			{
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) return;
				
				inside++;
				byte v = WorldHeightmap.SampleLayerU8(layerName, wx, wz); // Evaluate against raw bytes (>=1) to get covereage of painted vs unpainted
				if (v >= PaintThreshold) painted++;
			});
			return inside == 0 ? 0f : (float)painted / inside;
		}

		/// ┌────────────────────┐
		/// │ Scalar Hotspotting │ Modifies *ONLY* outside unpainted pixels, painted are written raw.
		/// └────────────────────┘ Incl. Blending colliding falloffs
		void ApplyHotspotScalar(IntDataMap2D target, string layerName, int regionX, int regionZ)
		{
			if (target == null) return;
			int size = target.Size; if (size <= 0) return;
			int tl = target.TopLeftPadding, br = target.BottomRightPadding;
			int inner = size - tl - br; if (inner <= 0) return;

			// If layer effectively covers the map, directly overwrite (replace) the vanilla map
			float coverage = ComputeInnerCoverageRaw(target, layerName, regionX, regionZ);
			if (coverage >= 0.95f) { OverwriteScalarMapFromLayer(target, layerName, regionX, regionZ); return; }

			int regionSize = ServerAPI!.WorldManager.RegionSize;
			int sizeSqrd = size * size;
			var data = target.Data ?? new int[sizeSqrd];

			// Seed mask and scaled hotspot values | Baseline is vanilla
			var seedMask	= new bool[sizeSqrd];
			var vHot		= new byte[sizeSqrd];
			var vBase		= new byte[sizeSqrd];

			ForEachRegionPixel(target, regionX, regionZ, (idx, wx, wz) =>
			{
				vBase[idx] = (byte)(data[idx] & 0xFF);
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) return;
				byte src = WorldHeightmap.SampleLayerU8Remapped(layerName, wx, wz);
				if (src >= PaintThreshold) { seedMask[idx] = true; vHot[idx] = src; }
			});

			// EDT (nearest seed distance + value), then blend toward nearest
			ComputeEDTAndNearestValue(size, seedMask, vHot, out var dist, out var nearVal);
			float pxToBlocks = (float)ServerAPI.WorldManager.RegionSize / inner;
			var outData = data; if (outData.Length != sizeSqrd) outData = new int[sizeSqrd];
			for (int index = 0; index < sizeSqrd; index++)
			{
				if (seedMask[index]) { outData[index] = vHot[index]; continue; } // preserve author intent
				float dpx = dist[index];
				if (float.IsInfinity(dpx)) continue; // no seeds anywhere
				float dBlocks = dpx * pxToBlocks;
				float t = GameMath.Clamp(dBlocks / VegetationInfluenceRadius, 0f, 1f);
				float w = 1f - Smooth01(t); // smooth falloff
				if (w <= 0f) continue; // out of influence, keep vanilla data
				outData[index] = LerpByte(vBase[index], nearVal[index], w);
			}

			target.Data = outData;
		}

		/// ┌───────────────────────────┐
		/// │ Climate Hotspotting & SDF │ Single channel,
		/// └───────────────────────────┘ 0=T, 1=R, 2=G/tectonics in low byte
		void ApplyHotspotClimateChannel(IntDataMap2D target, int regionX, int regionZ, string layerName, int ch)
		{
			if (target == null) return;
			int size = target.Size; if (size <= 0) return;
			int tl = target.TopLeftPadding, br = target.BottomRightPadding;
			int inner = size - tl - br; if (inner <= 0) return;

			// If esentially fully painted, use previous direct overwrite for this channel
			float coverage = ComputeInnerCoverageRaw(target, layerName, regionX, regionZ);
			if (coverage >= 0.95f)
			{
				OverwriteClimateMapFromLayers
				(
					target, regionX, regionZ,
					useTemp:     ch == 0,
					useMoisture: ch == 1,
					useTect:     ch == 2
				);
				return;
			}

			int regionSize = ServerAPI!.WorldManager.RegionSize;
			int sizeSqrd = size * size;
			var data = target.Data ?? new int[sizeSqrd];

			// Seed mask & hotspot channel values; baseline = current channel from packed TRG
			var seedMask	= new bool[sizeSqrd];
			var vHot		= new byte[sizeSqrd];
			var vBase		= new byte[sizeSqrd];

			// Populate baseline channel and seeds from the painted layer
			ForEachRegionPixel(target, regionX, regionZ, (idx, wx, wz) =>
			{
				int cur = data[idx];
				DecodeTRG(cur, out int tU8, out int rU8, out int gU8);
				vBase[idx] = (byte)(ch == 0 ? tU8 : (ch == 1 ? rU8 : gU8));
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) return;
				byte src = WorldHeightmap.SampleLayerU8Remapped(layerName, wx, wz); // Use fixed remapped 0-255 byte for painted seeds
				if (src >= PaintThreshold) { seedMask[idx] = true; vHot[idx] = src; }
			});

			// EDT distance + nearest seed value, then blend
			ComputeEDTAndNearestValue(size, seedMask, vHot, out var dist, out var nearVal);
			float pxToBlocks = (float)ServerAPI.WorldManager.RegionSize / inner;

			// Compose results back into packed TRG
			var outData = data; if (outData.Length != sizeSqrd) outData = new int[sizeSqrd];
			for (int idx = 0; idx < sizeSqrd; idx++)
			{
				int cur = (idx >= 0 && idx < data.Length) ? data[idx] : 0;
				DecodeTRG(cur, out int tU8, out int rU8, out int gU8);
				if (seedMask[idx])
				{
					if (ch == 0) tU8 = vHot[idx];
					else if (ch == 1) rU8 = vHot[idx];
					else gU8 = vHot[idx];
					outData[idx] = EncodeTRG(tU8, rU8, gU8);
					continue;
				}

				float dpx = dist[idx];
				if (!float.IsInfinity(dpx))
				{
					float dBlocks = dpx * pxToBlocks;
					float t = GameMath.Clamp(dBlocks / ClimateInfluenceRadius, 0f, 1f);
					float w = 1f - Smooth01(t);
					if (w > 0f) {
						int baseV = vBase[idx];
						int seedV = nearVal[idx];
						int v = (int)Math.Round(baseV + (seedV - baseV) * w);
						if (v < 0) v = 0; else if (v > 255) v = 255;
						if (ch == 0) tU8 = v; else if (ch == 1) rU8 = v; else gU8 = v;
					}
				}
				outData[idx] = EncodeTRG(tU8, rU8, gU8);
			}
			target.Data = outData;
		}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static float Smooth01(float t)
	{
		if (t <= 0f) return 0f;
		if (t >= 1f) return 1f;
		return t * t * (3f - 2f * t);
	}

	// Compute 2-pass chamfer distance + propagate nearest seed value
	static void ComputeEDTAndNearestValue(int size, bool[] seeds, byte[] seedVal, out float[] dist, out byte[] nearVal)
	{
		const float INF = 1e9f;
		const float D  = 1f; // 4-neighbour step
		const float D2 = 1.41421356f; // diagonal
		int n = size * size;
		dist    = new float[n];
		nearVal = new byte[n];
		for (int i = 0; i < n; i++)
		{
			if (seeds[i])	{ dist[i] = 0f; nearVal[i] = seedVal[i]; }
			else			{ dist[i] = INF; nearVal[i] = 0; }
		}
		// forward pass
		for (int z = 0; z < size; z++)
		for (int x = 0; x < size; x++)
		{
			int i = z * size + x;
			float di = dist[i];
			if (x > 0) { int j = i - 1; float nd = dist[j] + D; if (nd < di) { di = nd; nearVal[i] = nearVal[j]; } }							// (x-1, z)
			if (z > 0) { int j = i - size; float nd = dist[j] + D; if (nd < di) { di = nd; nearVal[i] = nearVal[j]; } }							// (x, z-1)
			if (x > 0 && z > 0) { int j = i - size - 1; float nd = dist[j] + D2; if (nd < di) { di = nd; nearVal[i] = nearVal[j]; } }			// (x-1, z-1)
			if (x + 1 < size && z > 0) { int j = i - size + 1; float nd = dist[j] + D2; if (nd < di) { di = nd; nearVal[i] = nearVal[j]; } }	// (x+1, z-1)
			dist[i] = di;
		}
		// backward pass
		for (int z = size - 1; z >= 0; z--)
		for (int x = size - 1; x >= 0; x--)
		{
			int i = z * size + x;
			float di = dist[i];
			byte  vi = nearVal[i];
			if (x + 1 < size) { int j = i + 1; float nd = dist[j] + D; if (nd < di) { di = nd; vi = nearVal[j]; } }								// (x+1, z)
			if (z + 1 < size) { int j = i + size; float nd = dist[j] + D; if (nd < di) { di = nd; vi = nearVal[j]; } }							// (x, z+1)
			if (x + 1 < size && z + 1 < size) { int j = i + size + 1; float nd = dist[j] + D2; if (nd < di) { di = nd; vi = nearVal[j]; } }		// (x+1, z+1)
			if (x > 0 && z + 1 < size) { int j = i + size - 1; float nd = dist[j] + D2; if (nd < di) { di = nd; vi = nearVal[j]; } }			// (x-1, z+1)
			dist[i] = di;
			nearVal[i] = vi;
		}
	}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void DecodeTRG(int packed, out int t, out int r, out int g) { t = (packed >> 16) & 0xFF; r = (packed >> 8) & 0xFF; g = packed & 0xFF; }
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int EncodeTRG(int t, int r, int g) => ((t & 0xFF) << 16) | ((r & 0xFF) << 8) | (g & 0xFF);
		#endregion

		#region Water
		void OnSeaFill(IChunkColumnGenerateRequest req) // Fill seas up to sea level, also set RainHeightMap accordingly (sea level - 1)
		{
			int chunkSize		= GlobalConstants.ChunkSize;
			int seaLevel		= ServerAPI!.World.SeaLevel;
			int baseX			= req.ChunkX * chunkSize;
			int baseZ			= req.ChunkZ * chunkSize;	
			var mapChunk		= GenTerraWorldAccessor!.GetMapChunk(req.ChunkX, req.ChunkZ); // mapchunk to bump RainHeightMap where underwater
			var rainMap			= mapChunk.RainHeightMap;
			var chunks			= req.Chunks;
			int freshWaterID	= ServerAPI.World.GetBlock(new AssetLocation("game:water-still-7")).BlockId;
			int saltWaterID		= ServerAPI.World.GetBlock(new AssetLocation("game:saltwater-still-7")).BlockId;

			for (int lz = 0; lz < chunkSize; lz++)
			for (int lx = 0; lx < chunkSize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) continue;
				int h = WorldHeightmap.SampleToWorldY(ServerAPI, wx, wz); if (h < 1) continue;

				if (h < seaLevel)
				{
					// Choose map water_ocean layer to saltwater, non-zero is ocean, zero is fresh water.
					byte oceanByte = WorldHeightmap.SampleLayerU8("water_ocean", wx, wz);
					bool ocean = oceanByte >= 1; // Change int for ocean threshold
					int waterId = ocean && saltWaterID != 0 ? saltWaterID : freshWaterID;
					if (waterId == 0) continue;
					for (int y = h + 1; y <= seaLevel - 1; y++)
					{
						int chunkY = y / chunkSize;
						int lY = y % chunkSize;
						if ((uint)chunkY >= (uint)chunks.Length) continue; // defensive
						var schunk = chunks[chunkY];
						if (schunk == null) continue; // defensive
						int index3d = (chunkSize * lY + lz) * chunkSize + lx;

						// Only fill fluid where the solid layer is air
						var cdata = schunk.Data;
						if (cdata.GetBlockIdUnsafe(index3d) == 0) { cdata.SetFluid(index3d, waterId); schunk.MarkModified(); }
					}

					int idx = lz * chunkSize + lx;
					ushort topWaterY = (ushort)Math.Max(1, seaLevel - 1);
					rainMap[idx] = topWaterY; // Vanilla expects rainheight at top water block
					if (topWaterY > mapChunk.YMax) mapChunk.YMax = topWaterY;
				}
			}

			mapChunk.MarkDirty();
		}

		// Beach Improvement Pass
		void OnBeachBand(IChunkColumnGenerateRequest req)
		{
			int chunkSize	= GlobalConstants.ChunkSize;
			int seaLevel	= ServerAPI!.World.SeaLevel;
			int baseX		= req.ChunkX * chunkSize, baseZ = req.ChunkZ * chunkSize;
			var mapChunk	= GenTerraWorldAccessor!.GetMapChunk(req.ChunkX, req.ChunkZ);
			var rainMap		= mapChunk?.RainHeightMap; if (rainMap == null) return;
			int regionSize	= ServerAPI.WorldManager.RegionSize;
			int sandId		= ServerAPI.World.GetBlock(new AssetLocation("game:sand"))?.BlockId ?? 0; if (sandId == 0) return;
			var chunks		= req.Chunks;
			for (int lz = 0; lz < chunkSize; lz++) for (int lx = 0; lx < chunkSize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				if (!WorldHeightmap!.ContainsWorld(wx, wz)) continue;
				bool nearWater = false; // cheap inland test, any neighbor within 6 blocks in this chunk is waterline/underwater?
				for (int dz = -6; dz <= 6 && !nearWater; dz++) for (int dx = -6; dx <= 6; dx++)
				{
					int nx = lx + dx, nz = lz + dz;
					if ((uint)nx >= (uint)chunkSize || (uint)nz >= (uint)chunkSize) continue;
					if (rainMap[nz * chunkSize + nx] <= seaLevel - 1) { nearWater = true; break; }
				}
				if (!nearWater) continue;
			   
				var region = ServerAPI.WorldManager.GetMapRegion(FloorDiv(wx, regionSize), FloorDiv(wz, regionSize));
				int beachU8 = SampleIntMapAtWorld(region.BeachMap, FloorDiv(wx, regionSize), FloorDiv(wz, regionSize), wx, wz) & 0xFF;
				if (beachU8 <= 128) continue;
				for (int y = seaLevel; y <= seaLevel + 2; y++) // place sand on dry cells from sea level to +2
				{
					int cy = y / chunkSize, ly = y % chunkSize; if ((uint)cy >= (uint)chunks.Length || chunks[cy] == null) continue;
					int idx3d = (chunkSize * ly + lz) * chunkSize + lx; var data = chunks[cy].Data;
					if (data.GetFluid(idx3d) != 0) continue; // skip non-fluid liquids
					if (data.GetBlockIdUnsafe(idx3d) != 0) data.SetBlockUnsafe(idx3d, sandId);
				}
			}
		}
		#endregion

		#region Vegetation
		// Refresh RainHeightMap after soils/layers so that stones, boulders, and other misc items get placed on the true surface
		void OnPreVegetationRefreshRain(IChunkColumnGenerateRequest req)
		{
			var blockAccessor	= (IBlockAccessor)GenTerraWorldAccessor!;
			int chunkSize		= GlobalConstants.ChunkSize;
			int mapY			= blockAccessor.MapSizeY;
			int baseX			= req.ChunkX * chunkSize;
			int baseZ			= req.ChunkZ * chunkSize;
			var mapChunk   		= GenTerraWorldAccessor!.GetMapChunk(req.ChunkX, req.ChunkZ);
			var rainMap 		= mapChunk.RainHeightMap;
			var heightMap 		= mapChunk.WorldGenTerrainHeightMap;
			
			var position = new BlockPos(Dimensions.NormalWorld);
			var chunks = req.Chunks;
			GenTerraWorldAccessor.BeginColumn();
			for (int lz = 0; lz < chunkSize; lz++)
			for (int lx = 0; lx < chunkSize; lx++)
			{
				int wx = baseX + lx, wz = baseZ + lz;
				int idx = lz * chunkSize + lx;
				
				// Scan downward to find first non-air (solid or liquid) block. We prefer the actual surface after layers.
				ushort foundSurfaceTop = 0; // first non-air
				ushort foundSolidTop   = 0; // first non-permeable (true ground)
				
				// Scan downward once, compute both tops
				for (int y = mapY - 2; y >= 1; y--)
				{
					position.Set(wx, y, wz);
					var block = blockAccessor.GetBlock(position);
					if (block == null) continue;
					if (foundSurfaceTop == 0)
					{
						if (block.BlockId != 0) { foundSurfaceTop = (ushort)y; }
						else
						{
							int chunkY = y / chunkSize, lY = y % chunkSize;
							if ((uint)chunkY < (uint)chunks.Length && chunks[chunkY] != null) {
								int idx3d = (chunkSize * lY + lz) * chunkSize + lx;
								if (chunks[chunkY].Data.GetFluid(idx3d) != 0) { foundSurfaceTop = (ushort)y; }
							}
						}
					}
					if (foundSolidTop   == 0 && !block.RainPermeable) { foundSolidTop = (ushort)y; break; }
				}
				
				// Raise terrain height to the visible surface (aligns decorators with heightmap)
				if (foundSurfaceTop > heightMap[idx]) heightMap[idx] = foundSurfaceTop;
				// Raise rain height to true ground (keep underwater values from SeaFill if higher)
				if (foundSolidTop > rainMap[idx]) rainMap[idx] = foundSolidTop;

				// Keep YMax conservative-high so lighting/vegetation have enough vertical range
				ushort ymax = heightMap[idx] > rainMap[idx] ? heightMap[idx] : rainMap[idx];
				if (ymax > mapChunk.YMax) mapChunk.YMax = ymax;
			}
			mapChunk.MarkDirty();
		}
		#endregion

		#region Ore & Special
		void ApplyOreMultiplier(IMapRegion region, int regionX, int regionZ) // Scales every ore's spawn maps using the ore_multiplier layer data
		{
			var dict = region?.OreMaps;
			if (dict == null || dict.Count == 0) return;

			foreach (var kv in dict)
			{
				var map2d = kv.Value;
				if (map2d == null) continue;
				int size = map2d.Size; if (size <= 0) continue;
				var data = map2d.Data ?? new int[size * size];

				ForEachRegionPixel(map2d, regionX, regionZ, (idx, wx, wz) =>
				{
					if (!WorldHeightmap!.ContainsWorld(wx, wz)) return;
					byte raw = WorldHeightmap.SampleLayerU8("ore_multiplier", wx, wz); // 0-15
					if (raw == 0) return; // fallthrough (no SDF for ore maps)
					float m = OreMulFromRaw(raw);
					int baseU8 = data[idx] & 0xFF;
					int scaled = baseU8 <= 0 ? 0 : (int)Math.Round(baseU8 * m);
					if (scaled < 0) scaled = 0; else if (scaled > 255) scaled = 255;
					data[idx] = (data[idx] & ~0xFF) | scaled;
				});
				map2d.Data = data;
			}
		}

		// Curve 8 = x1  | 15 = x4  | 0 = x0.25 (theoreical value only, 1 is our actual lowest value since we let 0 fall through)
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static float OreMulFromRaw(int raw)
		{
			const int neutral = 8; // == 50%
			if (raw >= neutral)	{ int steps = raw - neutral; return (float)Math.Pow(4f, steps / 7f); } // Step up multiplication
			else				{ int steps = neutral - raw; return (float)Math.Pow(0.25f, steps / 8f); } // Step down multiplication
		}
		#endregion

		#region Pre-Generation
		void ForceMapAreaGeneration(IServerPlayer player)
		{
			ServerAPI!.SendMessageToGroup
			(
				GlobalConstants.AllChatGroups,
				"WORLD PRE-GENERATION HAS STARTED.\n" +
				"DO NOT STOP THE SERVER UNTIL COMPLETE OR IT MIGHT CORRUPT THE SAVE FILE!",
				EnumChatType.Notification
			);
			ServerAPI.Logger.Event($"[worldpainter] Reminder: Modify your servermagicnumbers.json file to geatly speed up this process. See user guide.");
			ServerAPI.WorldManager.AutoGenerateChunks = false;

			// Stop gameplay until process is over
			if (player != null && player.WorldData.CurrentGameMode == EnumGameMode.Creative) // Ignore players in creative
			{
				player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
				player.WorldData.NoClip = false;
				player.WorldData.FreeMove = false;
				player.BroadcastPlayerData();
			}
			float originalTimeSpeed = ServerAPI.World.Calendar.CalendarSpeedMul;
			ServerAPI.InjectConsole("/time calendarspeedmul 0");

			int chunkSize = GlobalConstants.ChunkSize;
			int chunkPaddingBuffer = 8;
			int tileSize = 32;
			int minCx = FloorDiv(WorldHeightmap!.OriginX, chunkSize) - chunkPaddingBuffer;
			int minCz = FloorDiv(WorldHeightmap.OriginZ,  chunkSize) - chunkPaddingBuffer;
			int maxCx = FloorDiv(WorldHeightmap.OriginX + WorldHeightmap.Width  - 1, chunkSize) + chunkPaddingBuffer;
			int maxCz = FloorDiv(WorldHeightmap.OriginZ  + WorldHeightmap.Height - 1, chunkSize) + chunkPaddingBuffer;
			int total = (maxCx - minCx + 1) * (maxCz - minCz + 1);
			int done = 0, lastPct = -1;
			int curZ = minCz, curX = minCx;
			
			var pregenTimer = Stopwatch.StartNew();
			void PreGenerationComplete()
			{
				pregenTimer.Stop(); var elapsed = pregenTimer.Elapsed;
				string elapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\\:mm\\:ss") : elapsed.ToString(@"mm\\:ss");
				ServerAPI.SendMessageToGroup
				(
					GlobalConstants.AllChatGroups,
					"Finished pre-generating all chunks, your map has been fully written into the save file!\n" +
					$"It is now safe to log off, and/or disable the mod.\nProcess took {elapsedText} to complete.",
					EnumChatType.Notification
				);
				ServerAPI.Logger.Event($"[worldpainter] Generation Complete! Completion Time: {elapsedText}");

				ServerAPI.InjectConsole($"/time calendarspeedmul {originalTimeSpeed}");
				if (player != null && player.WorldData.CurrentGameMode == EnumGameMode.Creative)
				{ 
					player.WorldData.CurrentGameMode = EnumGameMode.Survival;
					player.BroadcastPlayerData();
				}

				// Safety, as otherwise streaming seems to fail after completion
				ServerAPI.WorldManager.AutoGenerateChunks = true;
				ServerAPI.WorldManager.SendChunks = true;
			}

			void NextTile()
			{
				if (curZ > maxCz) { PreGenerationComplete(); return; }

				int x1 = curX;
				int z1 = curZ;
				int x2 = Math.Min(maxCx, x1 + tileSize - 1);
				int z2 = Math.Min(maxCz, z1 + tileSize - 1);
				int count = (x2 - x1 + 1) * (z2 - z1 + 1);
				
				// Advance the scan
				curX = x2 + 1;
				if (curX > maxCx) { curX = minCx; curZ = z2 + 1; }
				
				var opts = new ChunkLoadOptions { KeepLoaded = false };
				opts.OnLoaded = () =>
				{
					done += count;
					int pct = (done * 100) / total;
					if (pct > lastPct)
					{
						ServerAPI.SendMessageToGroup(GlobalConstants.AllChatGroups, $"World Painter Generation Progress: {pct}%", EnumChatType.Notification);
						if (pct % 10 == 0) { ServerAPI.Logger.Event($"[worldpainter] Generation Progress: {pct}%"); } // Report to logs less frequently
						lastPct = pct;
					}
					NextTile();
				};
				ServerAPI.WorldManager.LoadChunkColumnPriority(x1, z1, x2, z2, opts);
			}

			NextTile();
		}
		#endregion

		#region Data Processing
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int ClampByteInt(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int LerpByte(int a, int b, float w) => ClampByteInt((int)Math.Round(a + (b - a) * w));

		// Nearest-neighbour sample of an IntDataMap2D at a world X/Z, honours padding
		int SampleIntMapAtWorld(IntDataMap2D map2d, int regionX, int regionZ, int wx, int wz)
		{
			if (map2d == null) return 0;
			int size = map2d.Size;
			if (size <= 0) return 0;
			int tl = map2d.TopLeftPadding;
			int br = map2d.BottomRightPadding;
			int inner = size - tl - br;
			if (inner <= 0) return 0;
			int regionSize = ServerAPI!.WorldManager.RegionSize;
			double fx = (wx - regionX * (double)regionSize) / regionSize;
			double fz = (wz - regionZ * (double)regionSize) / regionSize;
			int ix = GameMath.Clamp((int)Math.Floor(fx * inner) + tl, 0, size - 1);
			int iz = GameMath.Clamp((int)Math.Floor(fz * inner) + tl, 0, size - 1);
			var data = map2d.Data;
			if (data == null || data.Length == 0) return 0;
			return data[iz * size + ix];
		} 
		
		// True floor division for ints (handles negatives like Math.Floor) | Cast to double to avoid truncation-towards-zero
		static int FloorDiv(int a, int b) { return (int)Math.Floor(a / (double)b); } // b>0 for regionSize

		// Iterate all padded pixels of a region map, yielding (index, wx, wz)
		void ForEachRegionPixel(IntDataMap2D map2d, int regionX, int regionZ, Action<int,int,int> body)
		{
			if (map2d == null) return;
			int size = map2d.Size; if (size <= 0) return;
			int tl = map2d.TopLeftPadding, br = map2d.BottomRightPadding;
			int inner = size - tl - br; if (inner <= 0) return;
			int regionSize = ServerAPI!.WorldManager.RegionSize;
			for (int iz = 0; iz < size; iz++)
			{
				float fz = ((iz - tl) + 0.5f) / inner;
				int wz = (int)Math.Floor(regionZ * (double)regionSize + fz * regionSize);
				for (int ix = 0; ix < size; ix++)
				{
					float fx = ((ix - tl) + 0.5f) / inner;
					int wx = (int)Math.Floor(regionX * (double)regionSize + fx * regionSize);
					int index = iz * size + ix;
					body(index, wx, wz);
				}
			}
		}

		// Iterate only the inner (unpadded) pixels of a region map
		void ForEachRegionInnerPixel(IntDataMap2D map2d, int regionX, int regionZ, Action<int,int,int> body)
		{
			if (map2d == null) return;
			
			int size = map2d.Size; if (size <= 0) return;
			int tl = map2d.TopLeftPadding, br = map2d.BottomRightPadding;
			int inner = size - tl - br; if (inner <= 0) return;
			int regionSize = ServerAPI!.WorldManager.RegionSize;
		   
			for (int iz = tl; iz < size - br; iz++)
			{	
				float fz = ((iz - tl) + 0.5f) / inner;
				int wz = (int)Math.Floor(regionZ * (double)regionSize + fz * regionSize);
				for (int ix = tl; ix < size - br; ix++)
				{
					float fx = ((ix - tl) + 0.5f) / inner;
					int wx = (int)Math.Floor(regionX * (double)regionSize + fx * regionSize);
					int index = iz * size + ix;
					body(index, wx, wz);
				}
			}
		}
	}

	public sealed class HeightMap
	{
		public int Width  { get; private set; }
		public int Height { get; private set; }
		public int OriginX { get; private set; }
		public int OriginZ { get; private set; }
		public readonly int SampleMin, SampleMax; // 0-65535
		readonly ushort[] data; // row-major: Z rows, then X

		// WPHM metadata layers (LAYR)
		public sealed class Layer
		{
			public readonly string Name;
			public readonly int Width;
			public readonly int Height;
			public readonly byte Flags;
			public readonly byte[] Data; // row-major: Z then X, 1 byte per px (0-255)
			public Layer(string name, int w, int h, byte flags, byte[] data) { Name = name; Width = w; Height = h; Flags = flags; Data = data; }
		}
		readonly Dictionary<string, Layer> layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
		public bool HasLayer(string name) => layers.ContainsKey(name);
		public IEnumerable<string> LayerNames => layers.Keys;

		HeightMap(int w, int h, int ox, int oz, int smin, int smax, ushort[] d)
		{
			Width = w; Height = h; OriginX = ox; OriginZ = oz;
			SampleMin = smin; SampleMax = smax; data = d;
		}

		public static HeightMap Load(string path)
		{
			using var fileStream = File.OpenRead(path);
			using var binaryReader = new BinaryReader(fileStream);

			// 32-byte LE header
			byte[] magic = binaryReader.ReadBytes(4);
			if 
			(
				magic.Length != 4 ||
				magic[0] != (byte)'W' ||
				magic[1] != (byte)'P' ||
				magic[2] != (byte)'H' ||
				magic[3] != (byte)'M'
			) throw new Exception("Invalid or corrupted map file!"); // Note: Version checking was removed as unecessary

			binaryReader.ReadByte(); // versioning | Read regardless to consume byte without any use for it to prevent offsetting the bytestream
			binaryReader.ReadByte(); // reserved
			ushort headerSize = binaryReader.ReadUInt16();
			int width   = binaryReader.ReadInt32();
			int height  = binaryReader.ReadInt32();
			int originX = binaryReader.ReadInt32();
			int originZ = binaryReader.ReadInt32();
			int sampleMin = binaryReader.ReadUInt16();
			int sampleMax = binaryReader.ReadUInt16();
			binaryReader.ReadInt32(); // reserved

			if (headerSize != 32) throw new Exception($"Unexpected header size of {headerSize}");
			if (width <= 0 || height <= 0) throw new Exception("Invalid world bound values!");

			int count = width * height;
			var d = new ushort[count];
			for (int i = 0; i < count; i++) d[i] = binaryReader.ReadUInt16();
			var heightMap = new HeightMap(width, height, originX, originZ, sampleMin, sampleMax, d);

			// Expect LAYR chunks after samples
			while (fileStream.Position + 4 <= fileStream.Length)
			{
				byte[] magicChunk = binaryReader.ReadBytes(4);
				if (magicChunk.Length != 4) break;

				if (magicChunk[0]=='L' && magicChunk[1]=='A' && magicChunk[2]=='Y' && magicChunk[3]=='R')
				{
					ushort n = binaryReader.ReadUInt16();
					binaryReader.ReadUInt16(); // reserved
					for (int i = 0; i < n; i++)
					{
						int nameLen = binaryReader.ReadByte();
						byte flags = binaryReader.ReadByte();
						int lw = binaryReader.ReadInt32();
						int lh = binaryReader.ReadInt32();
						int dataLen = binaryReader.ReadInt32();
						string lname = System.Text.Encoding.UTF8.GetString(binaryReader.ReadBytes(nameLen));
						var payload = binaryReader.ReadBytes(dataLen);
						if (payload.Length != dataLen) throw new EndOfStreamException();
						if (lw != width || lh != height) throw new Exception($"Layer '{lname}' size mismatch! ({lw}x{lh} vs {width}x{height})");
						heightMap.layers[lname] = new Layer(lname, lw, lh, flags, payload);
					}
					continue;
				}
				fileStream.Seek(-4, SeekOrigin.Current); // Skip unknown chunks
				break;
			}
			return heightMap;
		}

		public void ApplyOffset(int ox, int oz) { OriginX += ox; OriginZ += oz; }
		public bool ContainsWorld(int wx, int wz) { return wx >= OriginX && wx < OriginX + Width && wz >= OriginZ && wz < OriginZ + Height; }
		public bool LayerNonZeroAt(string layerName, int wx, int wz, byte threshold = 1) { return SampleLayerU8(layerName, wx, wz) >= threshold; }

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte SampleLayerU8(string layerName, int wx, int wz)
		{
			// Combined layers get quantized to 0-16/0-15, they're not raw 0-255.
			if (!layers.TryGetValue(layerName, out var layer)) return 0;
			if (!ContainsWorld(wx, wz)) return 0;
			int lx = wx - OriginX;
			int lz = wz - OriginZ;
			int idx = lz * layer.Width + lx;
			return (idx >= 0 && idx < layer.Data.Length) ? layer.Data[idx] : (byte)0;
		}

		public int SampleToWorldY(ICoreServerAPI sapi, int wx, int wz)
		{
			// Each pixel is a block
			int xIndex = wx - OriginX;
			int zIndex = wz - OriginZ;
			if (xIndex < 0) xIndex = 0; else if (xIndex >= Width)  xIndex = Width - 1;
			if (zIndex < 0) zIndex = 0; else if (zIndex >= Height) zIndex = Height - 1;
			ushort v16 = data[zIndex * Width + xIndex];
			int mapY = sapi.World.BlockAccessor.MapSizeY;
			int y = v16; // Updated in release 1.5, reads ushort instead of byte
			if (y < 1) y = 1;
			if (y > mapY - 2) y = mapY - 2;

			return y;
		}

		/// ┌─────────────────────────┐ Combined layers are effectively 0-15.
		/// │ Fixed-domain Re-mapping │ We map those to 0-255 to try and retrieve the author's intent,
		/// └─────────────────────────┘ Any unlisted layer returns raw bytes as they are.
		static readonly Dictionary<string, int> fixedSrcMax = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			// Climate (packed TRG), each channel a layer
			["climate_temperature"]		= 15,
			["climate_moisture"]		= 15,
			["climate_tectonic"]		= 15,
			
			// Vegetation Densities
			["vegetation_forest"]		= 15,
			["vegetation_shrubbery"]	= 15,
			["vegetation_flowers"]		= 15,
			
			// Oceanicity & Beaches
			["water_ocean"] = 15,
			["water_beach"] = 15,
			
			// Ore map & other special maps aren't listed here, since they're not vanilla maps we can overwrite
		};

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte SampleLayerU8Remapped(string layerName, int wx, int wz)
		{
			byte raw = SampleLayerU8(layerName, wx, wz);
			if (!fixedSrcMax.TryGetValue(layerName, out int srcMax) || srcMax <= 0 || srcMax >= 255) return raw;
			int scaled = (raw * 255 + srcMax/2) / srcMax;
			if (scaled < 0) scaled = 0; else if (scaled > 255) scaled = 255;
			return (byte)scaled;
		}
		#endregion
	}
}
