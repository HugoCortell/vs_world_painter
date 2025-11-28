// Sparse Notes:
// Extent iterates the dimension rect, each pixel a block.
// Height: dimension.getIntHeightAt(x,z,0) u16 (was clamped to 255, then expanded to 16bits in old version)
// Custom Combined Metadata Layers: Fetched from App.getCustomLayers sampled via dimension.getLayerValueAt(layer, x, z) // Returns 0-255
// Unused layers are not written down. 
(function () {
	print("Processing map... This might take several minutes for large maps..."); print("");

	var TileSize = 128;
	var File = java.io.File;
	var RandomAccessFile = java.io.RandomAccessFile;
	var AppClass; try { AppClass = Packages.org.pepsoft.worldpainter.App; } catch (failed) { AppClass = org.pepsoft.worldpainter.App; }

	function SelectExportPath(defaultFileName) {
		var JFileChooser = Packages.javax.swing.JFileChooser;
		var JOptionPane = Packages.javax.swing.JOptionPane;
		var System = Packages.java.lang.System;
		var chooser = new JFileChooser(new File(System.getProperty("user.home")));
		chooser.setDialogTitle("Export WPHM file");
		chooser.setSelectedFile(new File(defaultFileName));

		var rc = chooser.showSaveDialog(null);
		if (rc !== JFileChooser.APPROVE_OPTION) return null;
		var f = chooser.getSelectedFile();
		var path = "" + f.getPath();
		if (!/\.wphm$/i.test(path)) f = new File(path + ".wphm");

		if (f.exists()) {
			var overwrite = JOptionPane.showConfirmDialog(
				null,
				"File already exists:\n" + f.getPath() + "\n\nOverwrite?",
				"Confirm overwrite",
				JOptionPane.YES_NO_OPTION
			);
			if (overwrite !== JOptionPane.YES_OPTION) return null;
		}
		return "" + f.getPath();
	}

	// Java byte[] allocator (Rhino-safe)
	var JArray = java.lang.reflect.Array;
	var BYTE_TYPE = java.lang.Byte.TYPE;
	function newByteArray(len) { return JArray.newInstance(BYTE_TYPE, len); }

	// Progress bar (prints only when percent changes)
	function repeatChar(ch, n) { var s = ""; for (var i = 0; i < n; i++) s += ch; return s; }
	function bar(pct, w) {
		if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
		var filled = Math.floor((pct * w) / 100);
		return "[" + repeatChar("▰", filled) + repeatChar("▱", w - filled) + "]";
	}
	function makeProgress(label) {
		var last = -1;
		return function (done, total) {
			var pct = total > 0 ? Math.floor((done * 100) / total) : 100;
			if (pct !== last) {
				last = pct;
				print(bar(pct, 28) + " " + pct + "% " + label + " (" + done + "/" + total + ")");
			}
		};
	}

	if (!dimension) { print("Export Failed: No dimension found, try closing the program and re-opening your world."); return; }

	var name = (world && world.getName && world.getName()) || (dimension && dimension.getName && dimension.getName()) || "vintagestoryworldexport";
	var outPath = (arguments && arguments.length > 0 && arguments[0]) || null;
	if (!outPath) {
		try { outPath = SelectExportPath(name + ".wphm"); } catch (e) { outPath = name + ".wphm"; }
		if (!outPath) { print("Export Failed: Can't create a file if we don't define where!"); return; }
	}

	// Dimension extent | per-block rectangle
	var rect = dimension.getExtent();
	if (!rect || rect.width <= 0 || rect.height <= 0) { print("Export Failed: Dimension has no tiles to export!"); return; }
	var minX   = (rect.x|0)      * TileSize;
	var minZ   = (rect.y|0)      * TileSize;
	var width  = (rect.width|0)  * TileSize;
	var height = (rect.height|0) * TileSize;

	// A note on fetching layers from worldpainter...
	// The normal methods shown on the API won't work, instead get CombinedLayers from App.getCustomLayers(),
	// and query them with dimension.getLayerValueAt(layer, x, z) over the dimension extent. Ignore getAllLayers(true) for this purpose, it hides them. 
	// List of custom layers
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
	function putLE32(raf, v) {
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

		// Height Pass (buffered per-row)
		var minH = 65535, maxH = 0;
		var heightRow = newByteArray(width * 2);
		var heightProg = makeProgress("Heightmap");

		for (var pz = 0, wz = minZ; pz < height; pz++, wz++) {
			if (wp && wp.interrupted) { print("Height Pass Interrupted."); return; }

			var bi = 0;
			for (var px = 0, wx = minX; px < width; px++, wx++) {
				var h = 0;
				try { h = Math.floor(dimension.getIntHeightAt(wx, wz, 0)); } catch (e) { h = 0; }
				if (h < 0) h = 0; else if (h > 65535) h = 65535;

				if (h < minH) minH = h;
				if (h > maxH) maxH = h;

				// 16-bit U16 little endian | low byte, high byte
				heightRow[bi++] = (h & 0xFF);
				heightRow[bi++] = ((h >>> 8) & 0xFF);
			}

			fileInMemory.write(heightRow);
			heightProg(pz + 1, height);
		}

		// Header & Versioning
		fileInMemory.seek(0);
		fileInMemory.writeBytes("WPHM");		// magic
		fileInMemory.writeByte(1);				// version (obsolete)
		fileInMemory.writeByte(0);				// reserved
		putLE16(fileInMemory, HEADER_SIZE);
		putLE32(fileInMemory, width);
		putLE32(fileInMemory, height);
		putLE32(fileInMemory, minX);
		putLE32(fileInMemory, minZ);
		putLE16(fileInMemory, minH);
		putLE16(fileInMemory, maxH);
		putLE32(fileInMemory, 0); // reserved

		// Fetch Custom Combined Layers from the app instance
		var app = null;
		try { app = AppClass.getInstanceIfExists ? AppClass.getInstanceIfExists() : AppClass.getInstance(); } catch (noapp) { app = null; }
		if (!app) { print("Export Failed: App not running in GUI mode. Partially written file dumped in: " + file.getAbsolutePath()); return; }

		var customSet = null;
		try { customSet = app.getCustomLayers(); } catch (nolayers) { customSet = null; }

		// Build candidates
		var candidates = []; // [{name, norm, obj, type}]
		try {
			if (customSet && customSet.iterator) {
				var itr = customSet.iterator();
				while (itr.hasNext()) {
					var layer = itr.next();
					if (!layer) continue;
					var layername = "" + layer.getName();
					var ltype = "" + layer.getClass().getSimpleName();
					var norm = layername.toLowerCase().replace(/[_\s-]+/g, "");
					candidates.push({ name: layername, norm: norm, obj: layer, type: ltype });
				}
			}
		} catch (badcandidates) {}

		function PrintBasicOutputInfo()
		{
			print(""); print("Successfully Exported World Painter WPHM file! Path: " + file.getAbsolutePath());
			print("Size: " + width + " x " + height + " px (blocks) @ (" + minX + ", " + minZ + ") Origin. Height: min=" + minH + " max=" + maxH);
		}

		function PrintCoopMessage()
		{
			print(""); print("If you're a modder, consider joining the Unofficial Vintage Story Modding Cooperative: https://discord.gg/XggWcvAqG9");
			print("We can develop better mods together than alone. We have dedicated channels for knowledge exchange and organization.");
		}

		if (!candidates.length) {
			PrintBasicOutputInfo();
			print("No metadata layers were found. Exported only heightmap.");
			PrintCoopMessage();
			return;
		}

		function resolveLayerByName(name) {
			var want = String(name).toLowerCase().replace(/[_\s-]+/g, "");
			var best = null;
			for (var i = 0; i < candidates.length; i++) {
				var c = candidates[i];
				if (c.norm === want) {
					if (c.type === "CombinedLayer") return c.obj;
					if (!best) best = c.obj;
				}
			}
			return best; // Can be null
		}

		// Plan layers to scan (skip missing)
		var missing = [];
		var zeroPainted = [];
		var foundStats = []; // {name, nonZero, min, max}

		var planned = []; // {name, obj}
		for (var li = 0; li < LAYER_NAMES.length; li++) {
			var lname = LAYER_NAMES[li];
			var lobj = resolveLayerByName(lname);
			if (!lobj) missing.push(lname);
			else planned.push({ name: lname, obj: lobj });
		}

		// Write LAYR chunk streaming (placeholder count, patched at end)
		var layerChunkStart = fileInMemory.length();
		var writtenLayers = 0;

		if (planned.length > 0) {
			fileInMemory.seek(layerChunkStart);
			fileInMemory.writeBytes("LAYR");
			putLE16(fileInMemory, 0); // placeholder count
			putLE16(fileInMemory, 0); // reserved

			for (var p = 0; p < planned.length; p++) {
				var plan = planned[p];
				var layerProg = makeProgress("Layer " + (p + 1) + "/" + planned.length + " " + plan.name);

				var payload = newByteArray(width * height);
				var idx = 0;
				var nonzero = 0, vmin = 255, vmax = 0;

				for (var pz2 = 0, wz2 = minZ; pz2 < height; pz2++, wz2++) {
					if (wp && wp.interrupted) {
						// remove partial LAYR chunk if interrupted mid-write
						fileInMemory.setLength(layerChunkStart);
						print("PROCESS INTERRUPTED. ABORTING!");
						return;
					}

					for (var px2 = 0, wx2 = minX; px2 < width; px2++, wx2++) {
						var v = 0;
						try { v = dimension.getLayerValueAt(plan.obj, wx2, wz2) | 0; } catch (e2) { v = 0; }
						if (v < 0) v = 0; else if (v > 255) v = 255;

						if (v !== 0) nonzero++;
						if (v < vmin) vmin = v;
						if (v > vmax) vmax = v;

						payload[idx++] = v;
					}

					layerProg(pz2 + 1, height);
				}

				if (nonzero > 0) {
					writtenLayers++;
					foundStats.push({
						name: plan.name,
						nonZero: nonzero,
						min: (vmin === 255 && vmax === 0) ? 0 : vmin,
						max: vmax
					});

					var nameBytes = new java.lang.String(plan.name).getBytes("UTF-8");
					var nameLen = Math.min(255, nameBytes.length);

					fileInMemory.writeByte(nameLen); // nameLen
					fileInMemory.writeByte(0);       // flags: 0 = raw U8
					putLE32(fileInMemory, width);
					putLE32(fileInMemory, height);
					putLE32(fileInMemory, payload.length);

					fileInMemory.write(nameBytes, 0, nameLen);
					fileInMemory.write(payload);
				} else {
					zeroPainted.push(plan.name);
				}
			}

			// If nothing was written, remove the empty LAYR chunk header
			if (writtenLayers === 0) {
				fileInMemory.setLength(layerChunkStart);
			} else {
				var endPos = fileInMemory.length();
				fileInMemory.seek(layerChunkStart + 4); // patch count
				putLE16(fileInMemory, writtenLayers);
				fileInMemory.seek(endPos);
			}
		}

		// Export Summary
		PrintBasicOutputInfo();
		if (missing.length) print("Missing Metadata Layers: " + missing.join(", "));
		if (zeroPainted.length) print("Unused Metadata Layers: " + zeroPainted.join(", "));

		if (foundStats.length === 0) { print("Metdata Layers Missing!"); }
		else {
			print("Layers Written: " + foundStats.length);
			var grand = 0;
			for (var k = 0; k < foundStats.length; k++) {
				var e = foundStats[k];
				var pct = (100.0 * e.nonZero / (width * height)).toFixed(2);
				grand += e.nonZero;
				print("	- " + e.name + " with coverage=" + e.nonZero + " (" + pct + "%)px range=[" + e.min + ".." + e.max + "]");
			}
			var grandPct = (100.0 * grand / (width * height)).toFixed(2);
			print("Overall Layer Map Covereage: " + grand + " (" + grandPct + "% of map)");
			PrintCoopMessage();
		}
	}
	finally { fileInMemory.close(); }
})();
