using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using CNCMaps.Engine.Map;
using CNCMaps.Engine.Rendering;
using CNCMaps.Engine.Drawables;
using CNCMaps.FileFormats;
using CNCMaps.FileFormats.Map;
using CNCMaps.FileFormats.VirtualFileSystem;
using CNCMaps.Shared;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace CNCMaps.Engine {
	public class StaticRenderer : IDisposable {
		static Logger _logger = LogManager.GetCurrentClassLogger();

		public StaticRenderer() {
			InitSettings();
			InitConfig();
			InitVfs();
			LoadIni();
			LoadPalette();
			LoadCollection();
		}

		public void Dispose() {
			_vfs.Dispose();
		}

		public bool ConfigureFromArgs(string[] args) {
			InitLoggerConfig();
			_settings.ConfigureFromArgs(args);

			if (_settings.Debug && !Debugger.IsAttached)
				Debugger.Launch();

			return ValidateSettings();
		}

		public bool ConfigureFromSettings(RenderSettings settings) {
			_settings = settings;
			return ValidateSettings();
		}

		public EngineResult Execute() {
			{
				string objName = "LTNK";
				//string owner, string name, short health, short direction, bool onBridge
				UnitObject unit = new UnitObject("none", objName, 100, 0x80, false);
				unit.Palette = _palettes.UnitPalette;
				RenderObject(unit, objName);
			}
			{
				string objName = "BRUTE";
				UnitObject unit = new UnitObject("none", objName, 100, 0x80, false);
				unit.Palette = _palettes.UnitPalette;
				RenderObject(unit, objName);
			}
			{
				string objName = "SHAD";
				UnitObject unit = new UnitObject("none", objName, 100, 0x80, false);
				unit.Palette = _palettes.UnitPalette;
				RenderObject(unit, objName);
			}
			{
				string objName = "LTNK";
				UnitObject unit = new UnitObject("none", objName, 100, 0x80, false);
				unit.Palette = _palettes.UnitPalette;
				RenderObject(unit, objName);
			}
			{
				string objName = "GACNST";
				StructureObject unit = new StructureObject("none", objName, 5000, 0x80);
				unit.Upgrade1 = unit.Upgrade2 = unit.Upgrade3 = "None";
				unit.Palette = _palettes.UnitPalette;
				RenderObject(unit, objName);
			}

			return EngineResult.RenderedOk;
		}

		private RenderSettings _settings = new RenderSettings();
		private ModConfig _config;
		private Game.PaletteCollection _palettes;
		private Game.GameCollection _tileTypes;
		private Game.ObjectCollection _animationTypes;
		private Game.ObjectCollection _vehicleTypes;
		private Game.ObjectCollection _infantryTypes;
		private Game.ObjectCollection _buildingTypes;
		private Game.ObjectCollection _aircraftTypes;
		private Game.ObjectCollection _overlayTypes;
		private Game.ObjectCollection _terrainTypes;
		private VirtualFileSystem _vfs;
		private IniFile _rules;
		private IniFile _art;

		private void RenderObject(GameObject obj, string name) {
			// Should be large enough to contain a single unit
			int width = 300;
			int height = 300;
			// As if the tile is at the center of screen
			MapTile dummyTile = new MapTile((ushort)(width/2/(_config.TileWidth/2)),
											(ushort)(height/2/(_config.TileHeight/2)), 0, 0, 0, 0, 0, 0, null);
			obj.Tile = dummyTile;
			obj.BottomTile = dummyTile;

			// Find the drawable of the type
			Drawable drawable = null;
			foreach (Game.GameCollection collection in new Game.GameCollection[] {
					_tileTypes,
					_animationTypes,
					_vehicleTypes,
					_infantryTypes,
					_buildingTypes,
					_aircraftTypes,
					_overlayTypes,
					_terrainTypes,
				}) {
				drawable = collection.GetDrawable(name);
				if (drawable != null) {
					break;
				}
			}
			if (drawable == null) {
				throw new Exception($"Not found drawable type of obj={name}");
			}
			DrawingSurface ds = new DrawingSurface(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
			obj.Drawable = drawable;
			drawable.Draw(obj, ds);
			// Cut the unit bound area
			if (_settings.SavePNG) {
				ds.SavePNG(Path.Combine(_settings.OutputDir, name + ".png"),
						   _settings.PNGQuality, new Rectangle(0, 0, width, height));
			}
		}

		private void InitSettings() {
			if (_settings.Engine == EngineType.AutoDetect) {
				_settings.Engine = EngineType.YurisRevenge;
				_logger.Info("Engine used for static renderer: {0}", _settings.Engine);
			}
		}

		private void InitConfig() {
			// Engine type is now definitive, load mod config
			_config = ModConfig.GetDefaultConfig(_settings.Engine);
			// from ModConfigEditor.cs:btnLoadYRTheaters_Click()
			_config.Theaters.Clear();
			TheaterSettings theaterSettings = new TheaterSettings {
				Type = TheaterType.Temperate,
				TheaterIni = "temperatmd.ini",
				Mixes = new List<string> {
					"isotemp.mix",
					"isotemmd.mix",
					"temperat.mix",
					"tem.mix",
				},
				Extension = ".tem",
				NewTheaterChar = 'T',
				IsoPaletteName = "isotem.pal",
				UnitPaletteName = "unittem.pal",
				OverlayPaletteName = "temperat.pal",
			};
			_config.Theaters.Add(theaterSettings);
			_config.SetActiveTheater(TheaterType.Temperate);
		}

		private void InitVfs() {
			_vfs = new VirtualFileSystem();
			// first add the dirs, then load the extra mixes, then scan the dirs
			foreach (string modDir in _config.Directories)
				_vfs.Add(modDir);

			// add mixdir to VFS (if it's not included in the mod config)
			if (!_config.Directories.Any()) {
				string mixDir =
							VirtualFileSystem.DetermineMixDir(_settings.MixFilesDirectory, _settings.Engine);
				_vfs.Add(mixDir);
			}

			foreach (string mixFile in _config.ExtraMixes)
				_vfs.Add(mixFile);

			_vfs.LoadMixes(_settings.Engine);

			foreach (string mix in ModConfig.ActiveTheater.Mixes)
				_vfs.Add(mix, CacheMethod.Cache); // we wish for these to be cached as they're gonna be hit often
		}

		private void LoadIni() {
			if (_settings.Engine == EngineType.RedAlert2 || _settings.Engine == EngineType.TiberianSun) {
				_rules = _vfs.Open<IniFile>("rules.ini");
				_art = _vfs.Open<IniFile>("art.ini");
			}
			else if (_settings.Engine == EngineType.YurisRevenge) {
				_rules = _vfs.Open<IniFile>("rulesmd.ini");
				_art = _vfs.Open<IniFile>("artmd.ini");
			}
			else if (_settings.Engine == EngineType.Firestorm) {
				_rules = _vfs.Open<IniFile>("rules.ini");
				var fsRules = _vfs.Open<IniFile>("firestrm.ini");
				_rules.MergeWith(fsRules);
				_art = _vfs.Open<IniFile>("artmd.ini");
			}
			else {
				throw new Exception($"unreachable engine type {_settings.Engine}");
			}

			_rules.LoadAresIncludes(_vfs);
		}

		private void LoadPalette() {
			// from Theater.cs:Initialize()
			// load palettes and additional mix files for this theater
			_palettes = new Game.PaletteCollection(_vfs);
			_palettes.IsoPalette = new Palette(_vfs.Open<PalFile>(ModConfig.ActiveTheater.IsoPaletteName));
			_palettes.OvlPalette = new Palette(_vfs.Open<PalFile>(ModConfig.ActiveTheater.OverlayPaletteName));
			_palettes.UnitPalette = new Palette(_vfs.Open<PalFile>(ModConfig.ActiveTheater.UnitPaletteName), ModConfig.ActiveTheater.UnitPaletteName, true);
			_palettes.IsoPalette.Recalculate();
			_palettes.OvlPalette.Recalculate();
			_palettes.UnitPalette.Recalculate();
			_palettes.AnimPalette = new Palette(_vfs.Open<PalFile>("anim.pal"));
			_palettes.AnimPalette.Recalculate();
		}

		private void LoadCollection() {
			_tileTypes = new Game.TileCollection(TheaterType.Temperate, _config, _vfs, _rules, _art, ModConfig.ActiveTheater);
			_animationTypes = new Game.ObjectCollection(CollectionType.Animation, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("Animations"), _palettes);
			_vehicleTypes = new Game.ObjectCollection(CollectionType.Vehicle, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("VehicleTypes"), _palettes);
			_infantryTypes = new Game.ObjectCollection(CollectionType.Infantry, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("InfantryTypes"), _palettes);
			_buildingTypes = new Game.ObjectCollection(CollectionType.Building, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("BuildingTypes"), _palettes);
			_aircraftTypes = new Game.ObjectCollection(CollectionType.Aircraft, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("AircraftTypes"), _palettes);
			_overlayTypes = new Game.ObjectCollection(CollectionType.Overlay, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("OverlayTypes"), _palettes);
			_terrainTypes = new Game.ObjectCollection(CollectionType.Terrain, TheaterType.Temperate,
				_config, _vfs, _rules, _art, _rules.GetSection("TerrainTypes"), _palettes);
		}

		private static void InitLoggerConfig() {
			if (LogManager.Configuration == null) {
				// init default config
				var target = new ColoredConsoleTarget();
				target.Name = "console";
				target.Layout = "${processtime:format=s\\.ffff} [${level}] ${message}";
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.Magenta,
					Condition = "level = LogLevel.Fatal"
				});
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.Red,
					Condition = "level = LogLevel.Error"
				});
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.Yellow,
					Condition = "level = LogLevel.Warn"
				});
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.Gray,
					Condition = "level = LogLevel.Info"
				});
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.DarkGray,
					Condition = "level = LogLevel.Debug"
				});
				target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule() {
					ForegroundColor = ConsoleOutputColor.White,
					Condition = "level = LogLevel.Trace"
				});
				LogManager.Configuration = new LoggingConfiguration();
				LogManager.Configuration.AddTarget("console", target);
#if DEBUG
				LogManager.Configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, target));
#else
				LogManager.Configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, target));

#endif
				LogManager.ReconfigExistingLoggers();
			}
			_logger = LogManager.GetCurrentClassLogger();
		}

		private bool ValidateSettings() {
			if (_settings.ShowHelp) {
				ShowHelp();
				return false; // not really false :/
			}
			else if (!File.Exists(_settings.InputFile)) {
				_logger.Error("Specified input file does not exist");
				return false;
			}
			else if (!_settings.SaveJPEG && !_settings.SavePNG && !_settings.SavePNGThumbnails  &&
				!_settings.GeneratePreviewPack && !_settings.FixupTiles && !_settings.FixOverlays && !_settings.CompressTiles &&
				!_settings.DiagnosticWindow) {
				_logger.Error("No action to perform. Either generate PNG/JPEG/Thumbnail or modify map or use preview window.");
				return false;
			}
			else if (_settings.OutputDir != "" && !Directory.Exists(_settings.OutputDir)) {
				_logger.Error("Specified output directory does not exist.");
				return false;
			}
			return true;
		}

		private void ShowHelp() {
			Console.ForegroundColor = ConsoleColor.Gray;
			Console.Write("Usage: ");
			Console.WriteLine("");
			var sb = new StringBuilder();
			var sw = new StringWriter(sb);
			_settings.GetOptions().WriteOptionDescriptions(sw);
			Console.WriteLine(sb.ToString());
		}

		public static bool IsLinux {
			get {
				int p = (int)Environment.OSVersion.Platform;
				return (p == 4) || (p == 6) || (p == 128);
			}
		}

		/// <summary>Gets the determine map name. </summary>
		/// <returns>The filename to save the map as</returns>
		public string DetermineMapName(MapFile map, EngineType engine, VirtualFileSystem vfs) {
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(map.FileName);

			IniFile.IniSection basic = map.GetSection("Basic");
			if (basic.ReadBool("Official") == false)
				return StripPlayersFromName(MakeValidFileName(basic.ReadString("Name", fileNameWithoutExtension)));

			string mapExt = Path.GetExtension(_settings.InputFile);
			string missionName = "";
			string mapName = "";
			PktFile.PktMapEntry pktMapEntry = null;
			MissionsFile.MissionEntry missionEntry = null;

			// campaign mission
			if (!basic.ReadBool("MultiplayerOnly") && basic.ReadBool("Official")) {
				string missionsFile;
				switch (engine) {
					case EngineType.TiberianSun:
					case EngineType.RedAlert2:
						missionsFile = "mission.ini";
						break;
					case EngineType.Firestorm:
						missionsFile = "mission1.ini";
						break;
					case EngineType.YurisRevenge:
						missionsFile = "missionmd.ini";
						break;
					default:
						throw new ArgumentOutOfRangeException("engine");
				}
				var mf = vfs.Open<MissionsFile>(missionsFile);
				if (mf != null)
					missionEntry = mf.GetMissionEntry(Path.GetFileName(map.FileName));
				if (missionEntry != null)
					missionName = (engine >= EngineType.RedAlert2) ? missionEntry.UIName : missionEntry.Name;
			}

			else {
				// multiplayer map
				string pktEntryName = fileNameWithoutExtension;
				PktFile pkt = null;

				if (FormatHelper.MixArchiveExtensions.Contains(mapExt)) {
					// this is an 'official' map 'archive' containing a PKT file with its name
					try {
						var mix = new MixFile(File.Open(_settings.InputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
						pkt = mix.OpenFile(fileNameWithoutExtension + ".pkt", FileFormat.Pkt) as PktFile;
						// pkt file is cached by default, so we can close the handle to the file
						mix.Close();

						if (pkt != null && pkt.MapEntries.Count > 0)
							pktEntryName = pkt.MapEntries.First().Key;
					}
					catch (ArgumentException) { }
				}

				else {
					// determine pkt file based on engine
					switch (engine) {
						case EngineType.TiberianSun:
						case EngineType.RedAlert2:
							pkt = vfs.Open<PktFile>("missions.pkt");
							break;
						case EngineType.Firestorm:
							pkt = vfs.Open<PktFile>("multi01.pkt");
							break;
						case EngineType.YurisRevenge:
							pkt = vfs.Open<PktFile>("missionsmd.pkt");
							break;
						default:
							throw new ArgumentOutOfRangeException("engine");
					}
				}


				// fallback for multiplayer maps with, .map extension,
				// no YR objects so assumed to be ra2, but actually meant to be used on yr
				if (mapExt == ".map" && pkt != null && !pkt.MapEntries.ContainsKey(pktEntryName) && engine >= EngineType.RedAlert2) {
					var mapVfs = new VirtualFileSystem();
					mapVfs.AddItem(_settings.InputFile);
					pkt = mapVfs.OpenFile<PktFile>("missionsmd.pkt");
				}

				if (pkt != null && !string.IsNullOrEmpty(pktEntryName))
					pktMapEntry = pkt.GetMapEntry(pktEntryName);
				pkt?.Dispose();
			}

			// now, if we have a map entry from a PKT file, 
			// for TS we are done, but for RA2 we need to look in the CSV file for the translated mapname
			if (engine <= EngineType.Firestorm) {
				if (pktMapEntry != null)
					mapName = pktMapEntry.Description;
				else if (missionEntry != null) {
					if (engine == EngineType.TiberianSun) {
						string campaignSide;
						string missionNumber;

						if (missionEntry.Briefing.Length >= 3) {
							campaignSide = missionEntry.Briefing.Substring(0, 3);
							missionNumber = missionEntry.Briefing.Length > 3 ? missionEntry.Briefing.Substring(3) : "";
							missionName = "";
							mapName = string.Format("{0} {1} - {2}", campaignSide, missionNumber.TrimEnd('A').PadLeft(2, '0'), missionName);
						}
						else if (missionEntry.Name.Length >= 10) {
							mapName = missionEntry.Name;
						}
					}
					else {
						// FS map names are constructed a bit easier
						mapName = missionName.Replace(":", " - ");
					}
				}
				else if (!string.IsNullOrEmpty(basic.ReadString("Name")))
					mapName = basic.ReadString("Name", fileNameWithoutExtension);
			}

			// if this is a RA2/YR mission (csfEntry set) or official map with valid pktMapEntry
			else if (missionEntry != null || pktMapEntry != null) {
				string csfEntryName = missionEntry != null ? missionName : pktMapEntry.Description;

				string csfFile = engine == EngineType.YurisRevenge ? "ra2md.csf" : "ra2.csf";
				_logger.Info("Loading csf file {0}", csfFile);
				var csf = vfs.Open<CsfFile>(csfFile);
				mapName = csf.GetValue(csfEntryName.ToLower());

				if (missionEntry != null) {
					if (mapName.Contains("Operation: ")) {
						string missionMapName = Path.GetFileName(map.FileName);
						if (char.IsDigit(missionMapName[3]) && char.IsDigit(missionMapName[4])) {
							string missionNr = Path.GetFileName(map.FileName).Substring(3, 2);
							mapName = mapName.Substring(0, mapName.IndexOf(":")) + " " + missionNr + " -" +
									  mapName.Substring(mapName.IndexOf(":") + 1);
						}
					}
				}
				else {
					// not standard map
					if ((pktMapEntry.GameModes & PktFile.GameMode.Standard) == 0) {
						if ((pktMapEntry.GameModes & PktFile.GameMode.Megawealth) == PktFile.GameMode.Megawealth)
							mapName += " (Megawealth)";
						if ((pktMapEntry.GameModes & PktFile.GameMode.Duel) == PktFile.GameMode.Duel)
							mapName += " (Land Rush)";
						if ((pktMapEntry.GameModes & PktFile.GameMode.NavalWar) == PktFile.GameMode.NavalWar)
							mapName += " (Naval War)";
					}
				}
			}

			// not really used, likely empty, but if this is filled in it's probably better than guessing
			if (mapName == "" && basic.SortedEntries.ContainsKey("Name"))
				mapName = basic.ReadString("Name");

			if (mapName == "") {
				_logger.Warn("No valid mapname given or found, reverting to default filename {0}", fileNameWithoutExtension);
				mapName = fileNameWithoutExtension;
			}
			else {
				_logger.Info("Mapname found: {0}", mapName);
			}

			mapName = StripPlayersFromName(MakeValidFileName(mapName)).Replace("  ", " ");
			return mapName;
		}

		private static string StripPlayersFromName(string mapName) {
			if (mapName.IndexOf(" (") != -1)
				mapName = mapName.Substring(0, mapName.IndexOf(" ("));
			else if (mapName.IndexOf(" [") != -1)
				mapName = mapName.Substring(0, mapName.IndexOf(" ["));
			return mapName;
		}

		/// <summary>Makes a valid file name.</summary>
		/// <param name="name">The filename to be made valid.</param>
		/// <returns>The valid file name.</returns>
		private static string MakeValidFileName(string name) {
			string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
			string invalidReStr = string.Format(@"[{0}]+", invalidChars);
			return Regex.Replace(name, invalidReStr, "_");
		}
	}
}
