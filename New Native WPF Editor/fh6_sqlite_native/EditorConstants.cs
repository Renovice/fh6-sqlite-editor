namespace FH6SQLiteEditorNative;

internal static class EditorConstants
{
    public static readonly string[] CoreTables =
    [
        "Data_Car",
        "NewProfile_Career_Garage"
    ];

    public static readonly string[] EngineTables =
    [
        "List_UpgradeEngine",
        "Data_Engine"
    ];

    public static readonly string[] EnginePartTables =
    [
        "List_UpgradeEngineIntake",
        "List_UpgradeEngineExhaust",
        "List_UpgradeEngineFuelSystem",
        "List_UpgradeEngineIgnition",
        "List_UpgradeEngineValves",
        "List_UpgradeEngineDisplacement",
        "List_UpgradeEnginePistonsCompression",
        "List_UpgradeEngineCamshaft",
        "List_UpgradeEngineFlywheel",
        "List_UpgradeEngineManifold",
        "List_UpgradeEngineRestrictorPlate",
        "List_UpgradeEngineOilCooling",
        "List_UpgradeEngineTurboSingle",
        "List_UpgradeEngineTurboTwin",
        "List_UpgradeEngineTurboQuad",
        "List_UpgradeEngineCSC",
        "List_UpgradeEngineDSC",
        "List_UpgradeEngineIntercooler"
    ];

    public static readonly string[] UpgradeTables =
    [
        "List_UpgradeAntiSwayFront",
        "List_UpgradeAntiSwayRear",
        "List_UpgradeBrakes",
        "List_UpgradeCarBody",
        "List_UpgradeCarBodyChassisStiffness",
        "List_UpgradeCarBodyTireAspectRatioFront",
        "List_UpgradeCarBodyTireAspectRatioRear",
        "List_UpgradeCarBodyTireWidthFront",
        "List_UpgradeCarBodyTireWidthRear",
        "List_UpgradeCarBodyTrackSpacingFront",
        "List_UpgradeCarBodyTrackSpacingRear",
        "List_UpgradeCarBodyWeight",
        "List_UpgradeDrivetrain",
        "List_UpgradeDrivetrainClutch",
        "List_UpgradeDrivetrainDifferential",
        "List_UpgradeDrivetrainDriveline",
        "List_UpgradeDrivetrainTransmission",
        "List_UpgradeMotor",
        "List_UpgradeMotorParts",
        "List_UpgradeRimSizeFront",
        "List_UpgradeRimSizeRear",
        "List_UpgradeSpringDamper",
        "List_UpgradeTireCompound"
    ];

    public static readonly Dictionary<string, (string LinkColumn, string LinkKind)> UpgradeTableLinks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["List_UpgradeAntiSwayFront"] = ("Ordinal", "car"),
            ["List_UpgradeAntiSwayRear"] = ("Ordinal", "car"),
            ["List_UpgradeBrakes"] = ("Ordinal", "car"),
            ["List_UpgradeCarBody"] = ("Ordinal", "car"),
            ["List_UpgradeDrivetrain"] = ("Ordinal", "car"),
            ["List_UpgradeRimSizeFront"] = ("Ordinal", "car"),
            ["List_UpgradeRimSizeRear"] = ("Ordinal", "car"),
            ["List_UpgradeSpringDamper"] = ("Ordinal", "car"),
            ["List_UpgradeTireCompound"] = ("Ordinal", "car"),
            ["List_UpgradeCarBodyChassisStiffness"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTireAspectRatioFront"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTireAspectRatioRear"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTireWidthFront"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTireWidthRear"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTrackSpacingFront"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyTrackSpacingRear"] = ("CarBodyID", "body"),
            ["List_UpgradeCarBodyWeight"] = ("CarBodyId", "body"),
            ["List_UpgradeDrivetrainClutch"] = ("DrivetrainID", "drivetrain"),
            ["List_UpgradeDrivetrainDifferential"] = ("DrivetrainID", "drivetrain"),
            ["List_UpgradeDrivetrainDriveline"] = ("DrivetrainID", "drivetrain"),
            ["List_UpgradeDrivetrainTransmission"] = ("DrivetrainID", "drivetrain"),
            ["List_UpgradeMotor"] = ("Ordinal", "car"),
            ["List_UpgradeMotorParts"] = ("MotorID", "motor")
        };

    public static readonly string[] DefaultTables =
    [
        "Data_Car",
        "List_UpgradeEngine",
        "Data_Engine",
        "List_UpgradeCarBody",
        "List_UpgradeDrivetrain",
        "List_UpgradeMotor",
        "Data_Motor",
        "NewProfile_Career_Garage"
    ];

    public static readonly string[] TuningLimitTables =
    [
        "NewProfile_Career_Garage",
        "List_UpgradeSpringDamper",
        "List_SpringDamperPhysics",
        "List_UpgradeAntiSwayFront",
        "List_UpgradeAntiSwayRear",
        "List_AntiSwayPhysics",
        "List_UpgradeBrakes",
        "List_UpgradeDrivetrainTransmission",
        "List_UpgradeDrivetrainDifferential",
        "List_UpgradeTireCompound",
        "List_TireCompound",
        "List_AeroPhysics",
        "List_UpgradeRearWing"
    ];

    public static readonly string[] AeroTables =
    [
        "List_AeroPhysics",
        "List_AeroStaticSystem",
        "List_UpgradeRearWing",
        "List_UpgradeCarBodyFrontBumper",
        "List_UpgradeCarBodyRearBumper",
        "List_UpgradeCarBodyHood",
        "List_UpgradeCarBodySideSkirt"
    ];

    public static readonly HashSet<string> AeroOptionTables =
    [
        "List_UpgradeRearWing",
        "List_UpgradeCarBodyFrontBumper",
        "List_UpgradeCarBodyRearBumper",
        "List_UpgradeCarBodyHood",
        "List_UpgradeCarBodySideSkirt"
    ];

    public static readonly string[] LookupTables =
    [
        "List_TireCompound",
        "List_TyreCurveDB",
        "List_TireFrictionCurve",
        "List_TireFrictionMultiCurve",
        "List_TireAffectCurve",
        "Combo_TireBrandCompound",
        "List_UpgradeTireCompoundFictionModOverride",
        "List_AntiSwayPhysics",
        "List_BrakeProfile",
        "List_BrakeType",
        "List_SpringDamperPhysics",
        "List_SteeringSettings",
        "List_RearSteeringSettings",
        "List_SuspensionPhysicsType",
        "List_ThirdSpringElement",
        "List_PreloadAndDroopDamper",
        "AutoSteerOverrides",
        "List_TractionControl",
        "VersionInfo"
    ];

    public static readonly HashSet<string> BoostEnginePartTables =
    [
        "List_UpgradeEngineTurboSingle",
        "List_UpgradeEngineTurboTwin",
        "List_UpgradeEngineTurboQuad"
    ];

    public static readonly HashSet<string> SuperchargerEnginePartTables =
    [
        "List_UpgradeEngineCSC",
        "List_UpgradeEngineDSC"
    ];

    public static bool IsAspirationEnginePartTable(string table)
    {
        return BoostEnginePartTables.Contains(table) || SuperchargerEnginePartTables.Contains(table);
    }

    public static int? ObservedEnginePartMenuMaxLevel(string table)
    {
        if (BoostEnginePartTables.Contains(table))
        {
            return 4;
        }
        if (SuperchargerEnginePartTables.Contains(table))
        {
            return 3;
        }
        if (table.Equals("List_UpgradeEngineRestrictorPlate", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (EnginePartTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            return 3;
        }
        return null;
    }

    public static IReadOnlyList<string> CompatibleEnginePartSourceTables(string table)
    {
        if (BoostEnginePartTables.Contains(table))
        {
            var preferred = new[]
            {
                table,
                "List_UpgradeEngineTurboTwin",
                "List_UpgradeEngineTurboSingle",
                "List_UpgradeEngineTurboQuad"
            };
            return preferred.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (SuperchargerEnginePartTables.Contains(table))
        {
            var preferred = new[]
            {
                table,
                "List_UpgradeEngineCSC",
                "List_UpgradeEngineDSC"
            };
            return preferred.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return [table];
    }
}
