using System.IO;
using System.Windows;

namespace FH6SQLiteEditorNative;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunSmokeTest(e.Args));
            return;
        }

        new MainWindow().Show();
    }

    private static int RunSmokeTest(string[] args)
    {
        try
        {
            try { File.Delete(Path.Combine(AppPaths.LocalStateDir, "last_smoke_error.txt")); } catch { }
            AppPaths.CleanOldSessions();
            var input = args.Length > 1 ? args[1] : AppPaths.DefaultDbPath;
            var output = args.Length > 2
                ? args[2]
                : Path.Combine(AppPaths.LocalStateDir, "native_smoke.sqlite");

            using var editor = new SqliteEditorService(input);
            var validation = editor.Validate();
            if (!validation.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            var importer = new GameDatabaseImporter();
            importer.PreviewChangedTables(editor.SessionPath, input, importAll: false);
            importer.PreviewChangedTables(editor.SessionPath, input, importAll: false);

            var cars = editor.SearchCars("skyline", "all");
            if (cars.Count == 0 || cars.Any(car => car.Title.StartsWith("_&", StringComparison.Ordinal)))
            {
                return 4;
            }
            if (!cars[0].Subtitle.Contains("Weight ", StringComparison.OrdinalIgnoreCase) ||
                !cars[0].Subtitle.Contains(" kg", StringComparison.OrdinalIgnoreCase))
            {
                return 19;
            }
            var testCar = cars[0];
            var before = editor.EngineSwapsForCar(testCar.Id);
            if (before.Count == 0 || editor.LoadEngineBase(before[0].EngineId).Rows.Count == 0)
            {
                return 5;
            }
            if (editor.TableExists("NewProfile_Career_Garage") &&
                editor.LoadTable("NewProfile_Career_Garage", testCar.Id).Rows.Cast<System.Data.DataRow>().Any(row => Convert.ToInt64(row["CarId"]) != testCar.Id))
            {
                return 9;
            }
            if (editor.TableExists("Data_Engine") && editor.LoadTable("Data_Engine", testCar.Id).Rows.Count > 1)
            {
                return 10;
            }
            if (editor.TableExists("Data_Car"))
            {
                var carRows = editor.LoadTable("Data_Car", testCar.Id);
                if (carRows.Rows.Count != 1 || !carRows.Columns.Contains("CurbWeightKg"))
                {
                    return 13;
                }
            }
            var engineSwapRows = editor.LoadEngineSwaps(testCar.Id);
            if (engineSwapRows.Rows.Count > 0 &&
                (!engineSwapRows.Columns.Contains("EngineMassDeltaKg") ||
                 !engineSwapRows.Columns.Contains("EffectiveMenuMassDiffKg")))
            {
                return 14;
            }
            if (engineSwapRows.Rows.Count > 0 && editor.TableExists("Data_Engine"))
            {
                var engineId = engineSwapRows.Rows[0]["EngineID"];
                if (editor.LoadRowsWhere("Data_Engine", "EngineID", engineId).Rows.Count != 1)
                {
                    return 16;
                }
            }
            if (editor.TableExists("List_SpringDamperPhysics") && editor.TableExists("List_UpgradeSpringDamper"))
            {
                var springRows = editor.LoadTable("List_SpringDamperPhysics", testCar.Id);
                if (springRows.Rows.Count > 0 &&
                    (!springRows.Columns.Contains("Axle") ||
                     !springRows.Columns.Contains("MinRideHeight") ||
                     !springRows.Columns.Contains("MaxRideHeight")))
                {
                    return 15;
                }
                if (springRows.Rows.Count > 0 &&
                    editor.LoadRowsWhere("List_SpringDamperPhysics", "SpringDamperPhysicsID", springRows.Rows[0]["SpringDamperPhysicsID"]).Rows.Count != 1)
                {
                    return 17;
                }
            }
            if (editor.TableExists("List_UpgradeTireCompound") && editor.TableExists("List_TireCompound"))
            {
                var tireRows = editor.LoadTable("List_UpgradeTireCompound", testCar.Id);
                if (tireRows.Rows.Count > 0 && !tireRows.Columns.Contains("TireCompoundName"))
                {
                    return 11;
                }

                var compoundRows = editor.LoadTable("List_TireCompound", null);
                if (!compoundRows.Columns.Contains("KnownUpgradeLabels") ||
                    !compoundRows.Rows.Cast<System.Data.DataRow>().Any(row =>
                        Convert.ToString(row["KnownUpgradeLabels"])?.Contains("Slick", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return 12;
                }
                if (tireRows.Rows.Count > 0 &&
                    editor.LoadRowsWhere("List_TireCompound", "TireCompoundID", tireRows.Rows[0]["TireCompoundID"]).Rows.Count != 1)
                {
                    return 18;
                }
            }
            var newEngine = editor.SearchEngineCatalog("")
                .FirstOrDefault(engine => before.All(existing => existing.EngineId != engine.EngineId));
            if (newEngine is not null)
            {
                var added = editor.AddEngineSwap(testCar.Id, newEngine.EngineId);
                var after = editor.EngineSwapsForCar(testCar.Id);
                if (!added || after.All(engine => engine.EngineId != newEngine.EngineId))
                {
                    return 6;
                }
            }
            var engineRows = editor.LoadEngineSwaps(testCar.Id);
            if (engineRows.Rows.Count > 0)
            {
                var beforeClone = engineRows.Rows.Count;
                editor.CloneTableRow("List_UpgradeEngine", engineRows.Rows[0]);
                var afterClone = editor.LoadEngineSwaps(testCar.Id).Rows.Count;
                if (afterClone <= beforeClone)
                {
                    return 8;
                }
            }
            var partTable = EditorConstants.EnginePartTables.FirstOrDefault(editor.TableExists);
            if (partTable is not null)
            {
                var templates = editor.EnginePartTemplates(partTable, before[0].EngineId);
                if (templates.Count > 0)
                {
                    var beforeParts = editor.LoadEngineParts(partTable, before[0].EngineId).Rows.Count;
                    var template = templates[0];
                    editor.AddEnginePartFromTemplate(partTable, before[0].EngineId, template.SourceTable, template.SourceId);
                    var afterParts = editor.LoadEngineParts(partTable, before[0].EngineId).Rows.Count;
                    if (afterParts <= beforeParts)
                    {
                        return 7;
                    }
                }
            }
            if (editor.TableExists("List_UpgradeEngineTurboQuad"))
            {
                var conversions = editor.AspirationConversionChoices(before[0].EngineId);
                var quad = conversions.FirstOrDefault(c => c.PartName.Equals("QuadTurbo", StringComparison.OrdinalIgnoreCase));
                if (quad is not null)
                {
                    var beforeParts = editor.LoadEngineParts("List_UpgradeEngineTurboQuad", before[0].EngineId).Rows.Count;
                    editor.AddAspirationConversion(before[0].EngineId, quad.AspirationId);
                    if (editor.LoadEngineParts("List_UpgradeEngineTurboQuad", before[0].EngineId).Rows.Count <= beforeParts)
                    {
                        return 20;
                    }
                }
            }
            if (editor.TableExists("List_UpgradeCarBodyTireWidthRear"))
            {
                var beforeRows = editor.LoadTable("List_UpgradeCarBodyTireWidthRear", testCar.Id).Rows.Count;
                editor.AddLinkedUpgradeOption("List_UpgradeCarBodyTireWidthRear", testCar.Id);
                if (editor.LoadTable("List_UpgradeCarBodyTireWidthRear", testCar.Id).Rows.Count <= beforeRows)
                {
                    return 21;
                }
            }
            if (editor.TableExists("List_UpgradeRearWing"))
            {
                var beforeRows = editor.LoadTable("List_UpgradeRearWing", testCar.Id).Rows.Count;
                editor.AddLinkedAeroOption("List_UpgradeRearWing", testCar.Id);
                if (editor.LoadTable("List_UpgradeRearWing", testCar.Id).Rows.Count <= beforeRows)
                {
                    return 22;
                }
            }
            editor.SaveAs(output);
            return File.Exists(output) && !File.Exists(output + "-wal") && !File.Exists(output + "-shm") ? 0 : 3;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.LocalStateDir, "last_smoke_error.txt"), ex.ToString());
            }
            catch
            {
            }
            return 1;
        }
    }
}
