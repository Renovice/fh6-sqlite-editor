namespace FH6SQLiteEditorNative;

internal enum FieldValueRoleKind
{
    Effect,
    Base,
    Selector,
    Reference,
    Display,
    Visual,
    Menu,
    Key,
    Derived,
    Unknown
}

internal sealed record FieldValueRole(FieldValueRoleKind Kind, string Label, string Description)
{
    public bool ShowBadge => Kind != FieldValueRoleKind.Unknown;
}

internal static class FieldValueRoles
{
    private static readonly FieldValueRole Effect = new(
        FieldValueRoleKind.Effect,
        "Effect",
        "Edit this when you want the selected part/physics row to actually behave differently. Example: MassDiff, tire width, torque scale, ride-height limits.");

    private static readonly FieldValueRole Base = new(
        FieldValueRoleKind.Base,
        "Base",
        "The stock/default car or engine value. Example: CurbWeight is the car's stock weight before upgrade rows add their own changes.");

    private static readonly FieldValueRole Selector = new(
        FieldValueRoleKind.Selector,
        "Selector",
        "This is an ID pointer. Open it to jump to the row that holds the real values.");

    private static readonly FieldValueRole Reference = new(
        FieldValueRoleKind.Reference,
        "Reference",
        "Catalog/source info used to explain or originally build other rows. Existing upgrade rows usually will not update just because this changes.");

    private static readonly FieldValueRole Display = new(
        FieldValueRoleKind.Display,
        "Display",
        "Mostly what the menu shows: names, PI/class, ratings, graph numbers, or precomputed stat text.");

    private static readonly FieldValueRole Visual = new(
        FieldValueRoleKind.Visual,
        "Visual",
        "Model, tire label/model, graphics flag, icon, thumbnail, paint, camera, or other visual/presentation data.");

    private static readonly FieldValueRole Menu = new(
        FieldValueRoleKind.Menu,
        "Menu",
        "Menu metadata such as price, level, stock flag, order, unlock/availability, or manufacturer. Usually not physics.");

    private static readonly FieldValueRole Key = new(
        FieldValueRoleKind.Key,
        "Key",
        "Database identity. Do not edit unless you are deliberately making/linking a new row.");

    private static readonly FieldValueRole Derived = new(
        FieldValueRoleKind.Derived,
        "Derived",
        "Read-only helper made by this editor. It explains the math/link, but you edit the original field next to it.");

    private static readonly FieldValueRole Unknown = new(
        FieldValueRoleKind.Unknown,
        "Unknown",
        "Not classified yet. Treat it as untested until you verify it in-game.");

    private static readonly Dictionary<string, HashSet<string>> TableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data_Car"] = Set("Id"),
        ["Data_Engine"] = Set("EngineID"),
        ["Data_Motor"] = Set("MotorID"),
        ["Data_CarBody"] = Set("Id", "CarBodyID", "CarBodyId"),
        ["Data_Drivetrain"] = Set("DrivetrainID"),
        ["List_TireCompound"] = Set("TireCompoundID"),
        ["List_TyreCurveDB"] = Set("TireCompoundID"),
        ["List_TireFrictionCurve"] = Set("FrictionCurveID"),
        ["List_TireFrictionMultiCurve"] = Set("FrictionMultiCurveID"),
        ["List_TireAffectCurve"] = Set("AffectCurveID"),
        ["List_TorqueCurve"] = Set("TorqueCurveID"),
        ["List_SpringDamperPhysics"] = Set("SpringDamperPhysicsID"),
        ["List_AntiSwayPhysics"] = Set("AntiSwayPhysicsID"),
        ["List_AeroPhysics"] = Set("AeroPhysicsID"),
        ["List_BrakeProfile"] = Set("BrakesProfileID"),
        ["List_PreloadAndDroopDamper"] = Set("PreloadAndDroopDamperID"),
        ["List_ThirdSpringElement"] = Set("ThirdSpringID"),
        ["List_SteeringSettings"] = Set("SteeringSettingsID"),
        ["List_RearSteeringSettings"] = Set("Id"),
        ["List_PartAttribute"] = Set("PartAttributeID")
    };

    public static FieldValueRole ForColumn(string? tableName, string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return Unknown;
        }

        var table = tableName ?? "";
        var c = Canonical(columnName);
        var t = table.Trim();
        var tableCanonical = Canonical(t);

        if (columnName.Equals("__fh6_rowid", StringComparison.OrdinalIgnoreCase))
        {
            return Key;
        }

        if (IsDerivedColumn(tableCanonical, c))
        {
            return Derived with
            {
                Description = DerivedDescription(c)
            };
        }

        if (IsOwnKey(t, columnName) ||
            (columnName.Equals("Id", StringComparison.OrdinalIgnoreCase) &&
             !tableCanonical.Equals("newprofilecareergarage", StringComparison.OrdinalIgnoreCase)))
        {
            return Key;
        }

        if (IsVisualColumn(c))
        {
            return Visual;
        }

        if (IsDisplayColumn(c))
        {
            return Display;
        }

        if (IsMenuColumn(c))
        {
            return Menu;
        }

        if (IsSelectorColumn(c))
        {
            return Selector;
        }

        if (tableCanonical.Equals("dataengine", StringComparison.OrdinalIgnoreCase))
        {
            if (c == "enginemasskg")
            {
                return Reference with
                {
                    Description = "Source engine catalog mass. It explains why MassDiff was authored, but engine swap weight comes from List_UpgradeEngine.MassDiff."
                };
            }

            return Base with
            {
                Description = "Stock/default engine definition. Upgrade rows and torque curves can have their own baked values."
            };
        }

        if (tableCanonical.Equals("datamotor", StringComparison.OrdinalIgnoreCase))
        {
            return Base;
        }

        if (tableCanonical.Equals("datacar", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDataCarBaseEffectColumn(c))
            {
                return Base with
                {
                    Description = c is "curbweight" or "curbweightkg"
                        ? "Base stock car weight. Upgrade rows add deltas on top of this instead of recalculating from engine mass."
                        : Base.Description
                };
            }

            if (IsDataCarAvailabilityColumn(c))
            {
                return Menu;
            }

            return Unknown;
        }

        if (tableCanonical.Equals("listpartattribute", StringComparison.OrdinalIgnoreCase) && c is "mass" or "dragscale" or "windinstabilityscale")
        {
            return Reference with
            {
                Description = "Part attribute source/reference value. Selected upgrade rows usually carry their own effect values."
            };
        }

        if (IsPhysicsOrCurveTable(tableCanonical))
        {
            return Effect;
        }

        if (tableCanonical.StartsWith("listupgrade", StringComparison.OrdinalIgnoreCase))
        {
            if (tableCanonical.Equals("listupgradecarbodyweight", StringComparison.OrdinalIgnoreCase) &&
                c is "mass" or "initialmass" or "cmheight" or "cmbackfront" or "cmleftright" or "blockdimx" or "blockdimy" or "blockdimz" or "yawoverride")
            {
                return Effect with
                {
                    Description = "Selected weight-reduction/body value. Mass is the actual kg value for that option."
                };
            }

            return Effect;
        }

        if (IsLikelyEffectColumn(c))
        {
            return Effect;
        }

        return Unknown;
    }

    private static bool IsDerivedColumn(string tableCanonical, string c)
    {
        if (tableCanonical.Equals("datacar", StringComparison.OrdinalIgnoreCase) && c == "curbweightkg")
        {
            return false;
        }
        if (tableCanonical.Equals("dataengine", StringComparison.OrdinalIgnoreCase) && c == "enginemasskg")
        {
            return false;
        }
        if (tableCanonical.Equals("listaerophysics", StringComparison.OrdinalIgnoreCase) &&
            c is "defaulttuneslider" or "drag0" or "downforce0" or "drag1" or "downforce1")
        {
            return false;
        }

        return c is "effectivemenumassdiffkg" or "enginemassdeltakg" or
                   "enginemasskg" or "stockenginemasskg" or "effectivemassdiffkg" or
                   "enginemedianame" or "tirecompoundname" or "knownupgradelabels" or
                   "carsusingcompound" or "basedefaultpressure" or "baselatfrictionscale" or
                   "basebrakefrictionscale" or "baseaccelfrictionscale" or "basewetfrictionscale" or
                   "baserollresistance" or "brakeprofilename" or
                   "frontdefrideheight" or "frontminrideheight" or "frontmaxrideheight" or
                   "reardefrideheight" or "rearminrideheight" or "rearmaxrideheight" or
                   "frontdefspringrate" or "frontminspringrate" or "frontmaxspringrate" or
                   "reardefspringrate" or "rearminspringrate" or "rearmaxspringrate" or
                   "frontdefswaybar" or "frontminswaybar" or "frontmaxswaybar" or "frontswaybardamping" or
                   "reardefswaybar" or "rearminswaybar" or "rearmaxswaybar" or "rearswaybardamping" or
                   "part" or "axle" or "upgradeid" or "upgradelevel" or "upgradeisstock" or
                   "defaulttuneslider" or "drag0" or "downforce0" or "drag1" or "downforce1";
    }

    private static string DerivedDescription(string c)
    {
        return c switch
        {
            "curbweightkg" => "Editor kg view of Data_Car.CurbWeight. Editing it writes kg / 100 back to CurbWeight.",
            "effectivemenumassdiffkg" => "Read-only copy of MassDiff. Edit MassDiff to change engine swap menu weight.",
            "enginemassdeltakg" => "Read-only engine mass comparison. Existing MassDiff rows do not recalculate from this.",
            "enginemasskg" or "stockenginemasskg" => "Read-only Data_Engine mass shown for context. Engine swap effect is still MassDiff.",
            "effectivemassdiffkg" => "Read-only Mass - InitialMass helper for weight-reduction rows. Edit Mass if you want the option to weigh less/more.",
            _ => Derived.Description
        };
    }

    private static bool IsOwnKey(string table, string columnName)
    {
        return TableKeys.TryGetValue(table, out var keys) && keys.Contains(columnName);
    }

    private static bool IsVisualColumn(string c)
    {
        return c.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("image", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("graphics", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("caliperrgb", StringComparison.OrdinalIgnoreCase) ||
               c is "tiremodelname" or "wet tiremodelname" or "wettiremodelname" or "wingmask" or "modelid";
    }

    private static bool IsDisplayColumn(string c)
    {
        return c.Contains("medianame", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("displayname", StringComparison.OrdinalIgnoreCase) ||
               c is "name" or "enginename" or "modelshort" or "makename" or "makeicon" or
                   "partstringid" or "partsstringid" or "partsstringid" or "contentid" or
                   "performanceindex" or "pi" or "classid" ||
               c.Contains("graphing", StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith("sim", StringComparison.OrdinalIgnoreCase) ||
               c.EndsWith("rating", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("time", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("topspeed", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("quartermile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMenuColumn(string c)
    {
        return c is "price" or "basecost" or "manufacturerid" or "level" or "isstock" or
                   "releaseorder" or "sequence" or "baserarity" or "ispurchased" or "isunicorn" or
                   "isinstalled" or "isarcade" or "isdrivable" or "visibleonlyifowned" or
                   "donotallowremovalfromgarage" ||
               c.Contains("notavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSelectorColumn(string c)
    {
        return c is "ordinal" or "carid" or "carbodyid" or "carbodyid" or "engineid" or "motorid" or
                   "drivetrainid" or "tirecompoundid" or "tirebrandid" or "brakesprofileid" or
                   "brakeprofile" or "aerophysicsid" or "frontspringdamperphysicsid" or
                   "rearspringdamperphysicsid" or "antiswayphysicsid" or "torquecurvefullthrottleid" or
                   "frictionmulticurvelateralid" or "frictionmulticurvelateralidoffroad" or
                   "frictionmulticurvelongitudinalaccelid" or "frictionmulticurvelongitudinalaccelidoffroad" or
                   "frictionmulticurvelongitudinalbrakeid" or "frictionmulticurvelongitudinalbrakeidoffroad" or
                   "autosteeroverrideid" or "speedlimiterid" or "tcprofileid" or "differentialprofileid" ||
               (c.EndsWith("id", StringComparison.OrdinalIgnoreCase) && !c.EndsWith("stringid", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDataCarBaseEffectColumn(string c)
    {
        return c is "curbweight" or "curbweightkg" or "weightdistribution" or
                   "fronttirewidthmm" or "reartirewidthmm" or "fronttireaspect" or "reartireaspect" or
                   "frontwheeldiameterin" or "rearwheeldiameterin" or
                   "frontstockrideheight" or "rearstockrideheight" or
                   "bodyaerolongitudinaldrag" or "bodyaeroverticaldrag" or
                   "bodyaerolateraldragfront" or "bodyaerolateraldragrear" or
                   "bodyaeroforwarddownforcefront" or "bodyaeroforwarddownforcerear" or
                   "bodyaeroanglezerodownforce" or "bodyaerowiforcescale" or
                   "gametorquescale" or "gamedragscale" or "gamedownforcescale" or "gamedownforcescaleoffroad" or
                   "frontdownforceclampkg" or "reardownforceclampkg" or
                   "tractionroad" or "tractionoffroad" or "tractionsnow" or
                   "latfrontscalarmax" or "latfrontscalarclamp" or "latrearscalarmax" or "latrearscalarclamp" or
                   "longaccelfrontscalarmax" or "longaccelfrontscalarclamp" or
                   "longaccelrearscalarmax" or "longaccelrearscalarclamp" or
                   "longbrakefrontscalarmax" or "longbrakefrontscalarclamp" or
                   "longbrakerearscalarmax" or "longbrakerearscalarclamp";
    }

    private static bool IsDataCarAvailabilityColumn(string c)
    {
        return c.StartsWith("is", StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith("has", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("available", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("visible", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPhysicsOrCurveTable(string tableCanonical)
    {
        return tableCanonical.Contains("physics", StringComparison.OrdinalIgnoreCase) ||
               tableCanonical.Contains("curve", StringComparison.OrdinalIgnoreCase) ||
               tableCanonical is "listtirecompound" or "listtyrecurvedb" or "listbrakeprofile" or
                   "listtractioncontrol" or "liststeeringsettings" or "listrearsteeringsettings" or
                   "autosteeroverrides" or "listthirdspringelement" or "listpreloadanddroopdamper";
    }

    private static bool IsLikelyEffectColumn(string c)
    {
        return c.Contains("mass", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("weight", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("drag", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("wind", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("torque", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("power", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("boost", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("rpm", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("gear", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("slip", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("brake", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("friction", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("swaybar", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("spring", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("dampen", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("rideheight", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("downforce", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("pressure", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("camber", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("caster", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("toe", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("width", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("track", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("diameter", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("aspect", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("ratio", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("inertia", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("stiffness", StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
