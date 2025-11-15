// Sparse Notes:
// Extent iterates the dimension rect, each pixel a block.
// Height: dimension.getIntHeightAt(x,z,0) clamped to 255, then expanded to 16bits.
// Custom Combined Metadata Layers: Fetched from App.getCustomLayers sampled via dimension.getLayerValueAt(layer, x, z) // Returns 0-255
// Unused layers are not written down.
(function () {
	print("Processing map... This might take several minutes for large maps...");
	print("");
	print("While you wait, consider joining the Unofficial Vintage Story Modding Cooperative: https://discord.gg/XggWcvAqG9");
	print("As modders, we can help each other make even better stuff. Plus we have dedicated channels for better organization.");
	print("");
	print("");

	var TileSize = 128;
	var File = java.io.File;
	var RandomAccessFile = java.io.RandomAccessFile;
	var ByteArrayOutputStream = java.io.ByteArrayOutputStream;
	var AppClass; try { AppClass = Packages.org.pepsoft.worldpainter.App; } catch (failed) { AppClass = org.pepsoft.worldpainter.App; }
	if (!dimension) { print("Export Failed: No dimension found, try closing the program and re-opening your world."); return; }
	var name = (world && world.getName && world.getName()) || (dimension && dimension.getName && dimension.getName()) || "vintagestoryworldexport";
	var outPath = (arguments && arguments.length > 0 && arguments[0]) || (name + ".wphm");

	// Dimension extent | per-block rectangle
	var rect = dimension.getExtent();
	if (!rect || rect.width <= 0 || rect.height <= 0) { print("Export Failed: Dimension has no tiles to export!"); return; }
	var minX	= (rect.x|0)		* TileSize;
	var minZ	= (rect.y|0)		* TileSize;
	var width	= (rect.width|0)	* TileSize;
	var height	= (rect.height|0)	* TileSize;

	// A note on fetching layers from worldpainter...
	// The normal methods shown on the API won't work, instead get CombinedLayers from App.getCustomLayers(),
	// and query them with dimension.getLayerValueAt(layer, x, z) over the dimension extent. Ignore getAllLayers(true) for this purpose, it hides them.

	// List of custom layers to look for via App.getCustomLayers()
	var LAYER_NAMES = [
		"climate_temperature",
		"climate_moisture",
		"climate_tectonic",

		"vegetation_forest",
		"vegetation_shrubbery",
		"vegetation_flowers",

		"water_ocean",
		"water_beach",

		"ore_multiplier"
	];

	// Little Endian Helpers
	function putLE16(raf, v) { raf.writeByte(v & 0xFF); raf.writeByte((v >>> 8) & 0xFF); }
	function putLE32(raf, v)
	{ 
		raf.writeByte(v & 0xFF); raf.writeByte((v >>> 8) & 0xFF);
		raf.writeByte((v >>> 16) & 0xFF); raf.writeByte((v >>> 24) & 0xFF);
	}

	// Open Output
	var file = new File(outPath);
	if (file.getParentFile()) file.getParentFile().mkdirs();
	var fileInMemory = new RandomAccessFile(file, "rw");

	try {
		// Reserve header
		var HEADER_SIZE = 32;
		fileInMemory.setLength(0);
		fileInMemory.seek(HEADER_SIZE);

		// Height Pass
		var minH =	999999, maxH = -999999;
		for (var pz = 0; pz < height; pz++) {
			if (wp && wp.interrupted) { print("Height Pass Interrupted."); return; }
			var wz = minZ + pz;

			for (var px = 0; px < width; px++) {
			var wx = minX + px;

			var h = 0;
			try { h = dimension.getIntHeightAt(wx, wz, 0)|0; } catch (e) { h = 0; }
			if (h < 0) h = 0; if (h > 255) h = 255;

			if (h < minH) minH = h;
			if (h > maxH) maxH = h;

			var v16 = (h << 8) | h; // expand to 16-bit
			fileInMemory.writeByte(v16 & 0xFF); // LE uint16
			fileInMemory.writeByte((v16 >>> 8) & 0xFF);
		}
	}

	// Header & Versioning
	fileInMemory.seek(0);
	fileInMemory.writeBytes("WPHM"); // Used to verify legitimate files
	fileInMemory.writeByte(1); // Obsolete, versioning
	fileInMemory.writeByte(0); // reserved
	putLE16(fileInMemory, HEADER_SIZE); // header size
	putLE32(fileInMemory, width);
	putLE32(fileInMemory, height);
	putLE32(fileInMemory, minX);
	putLE32(fileInMemory, minZ);
	putLE16(fileInMemory, (minH << 8) | minH);
	putLE16(fileInMemory, (maxH << 8) | maxH);
	putLE32(fileInMemory, 0); // reserved

	// Fetch Custom Combined Layers from the app instance
	var app = null;
	try { app = AppClass.getInstanceIfExists ? AppClass.getInstanceIfExists() : AppClass.getInstance(); } catch (noapp) { app = null; }
	if (!app) { print("Export Failed: App not running in GUI mode. Partially written file dumped in: "+ file.getAbsolutePath()); return; }
	var customSet = null;
	try { customSet = app.getCustomLayers(); } catch (nolayers) { customSet = null; }

	// Build a list of candidate layers from set
	var candidates = []; // [{name, norm, obj, type}]
	try {
		if (customSet && customSet.iterator) {
			var itr = customSet.iterator();
			while (itr.hasNext()) 
			{
				var layer = itr.next();
				if (!layer) continue;
				var layername = "" + layer.getName();
				var ltype = "" + layer.getClass().getSimpleName();
				var norm = layername.toLowerCase().replace(/[_\s-]+/g, "");
				candidates.push({ name: layername, norm: norm, obj: layer, type: ltype });
			}
		}
	} catch (badcandidates) {}

	if (!candidates.length) {
		print("Exported world with only terrain heightmap data. No metadata layers were found.");
		print("Path: " + file.getAbsolutePath());
		print("Size: " + width + " x " + height + " px");
		print("Origin: (" + minX + ", " + minZ + ")");
		print("Height: min=" + minH + " max=" + maxH);
		return;
	 }

	// Flexible Resolver | Matches by name ignoring case and separators, prefer CombinedLayer if duplicate name somehow exist
	function resolveLayerByName(name) {
		var want = String(name).toLowerCase().replace(/[_\s-]+/g, "");
		var best = null;
		for (var i=0;i<candidates.length;i++)
		{
			var c = candidates[i];
			if (c.norm === want)
			{ 
				if (c.type === "CombinedLayer") return c.obj;
				if (!best) best = c.obj;
			}
		}
		return best; // Can be null
	}

	// Collect painted layers (non-zero coverage only)
	var found = [];			// {name, payload(byte[]), nonZero, min, max}
	var missing = [];		// If requested but not present
	var zeroPainted = [];	// Present but 0 coverage found across the map (unpainted/unused layer by artist)
	var requested = LAYER_NAMES.length;
	for (var i = 0; i < LAYER_NAMES.length; i++)
	{
		var layername = LAYER_NAMES[i];
		var layerobject = resolveLayerByName(layername);
		if (!layerobject) { missing.push(layername); continue; } // Oops! Undefined!

		var byteoutputstreamarray = new ByteArrayOutputStream(width * height);
		var nonzero = 0, vmin = 255, vmax = 0;

		for (var pz2 = 0; pz2 < height; pz2++)
		{
			if (wp && wp.interrupted) { print("PROCESS INTERRUPTED. ABORTING!"); return; }
			var wz2 = minZ + pz2;

			for (var px2 = 0; px2 < width; px2++)
			{
				var wx2 = minX + px2;
				var v = 0;
				
				try { v = dimension.getLayerValueAt(layerobject, wx2, wz2)|0; } catch (e) { v = 0; }
				if (v < 0) v = 0; if (v > 255) v = 255;

				if (v !== 0) nonzero++;
				if (v < vmin) vmin = v;
				if (v > vmax) vmax = v;
				byteoutputstreamarray.write(v);
			}
		}

		if (nonzero > 0)
		{
			found.push
			({
				name: layername,
				payload: byteoutputstreamarray.toByteArray(),
				nonZero: nonzero,
				min: (vmin === 255 && vmax === 0) ? 0 : vmin,
				max: vmax
			});
		}
		else { zeroPainted.push(layername); }
	}

	// Append painted LAYR chunks
	if (found.length > 0) {
		fileInMemory.seek(fileInMemory.length());
		fileInMemory.writeBytes("LAYR");
		putLE16(fileInMemory, found.length);
		putLE16(fileInMemory, 0); // reserved

		for (var j = 0; j < found.length; j++)
		{
			var ent = found[j];
			var nameBytes = new java.lang.String(ent.name).getBytes("UTF-8");
			var payload = ent.payload;

			fileInMemory.writeByte(Math.min(255, nameBytes.length)); // nameLen
			fileInMemory.writeByte(0); // flags: 0 = raw U8
			putLE32(fileInMemory, width);
			putLE32(fileInMemory, height);
			putLE32(fileInMemory, payload.length);

			fileInMemory.write(nameBytes, 0, Math.min(255, nameBytes.length));
			fileInMemory.write(payload);
		}
	}

	// Export Summary
	print("Successfully Exported World Painter WPHM file!");
	print("Path: " + file.getAbsolutePath());
	print("Size: " + width + " x " + height + " px (blocks)");
	print("Origin: (" + minX + ", " + minZ + ")");
	print("Height: min=" + minH + " max=" + maxH + " (stored as 16-bit expanded)");
	//print("Metadata Layers: " + requested); // debug only, commented out in release
	if (missing.length) print("Missing Metadata Layers: " + missing.join(", "));
	if (zeroPainted.length) print("Unused Metadata Layers: " + zeroPainted.join(", "));
	if (found.length === 0) { print("Metdata Layers Missing!"); } 
	else {
		print("Layers Written: " + found.length);
		var grand = 0;
		for (var k = 0; k < found.length; k++) {
			var e = found[k];
			var pct = (100.0 * e.nonZero / (width * height)).toFixed(2);
			grand += e.nonZero;
			print("	- " + e.name + " with coverage=" + e.nonZero + " (" + pct + "%)px range=[" + e.min + ".." + e.max + "]");
		}
		var grandPct = (100.0 * grand / (width * height)).toFixed(2);
		print("Overall Layer Map Covereage: " + grand + " (" + grandPct + "% of map)");
	} } finally { fileInMemory.close(); }
})();
