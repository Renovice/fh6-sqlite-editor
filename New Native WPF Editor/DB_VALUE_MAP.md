# FH6 SQLite Value Map

This is based on the local `fh6_db.sqlite` schema and row relationships.

## Editor value roles

The native editor now tags fields with these roles:

- `Effect`: a selected upgrade/physics/tuning value that is likely read directly for menu or in-game behavior.
- `Base`: a base car/engine definition value. These can matter, but they are not the same as already-authored upgrade rows.
- `Selector`: an ID that points into another table. The linked row usually holds the real values.
- `Reference`: an authoring/source value. Existing upgrade rows usually do not recalculate from it automatically.
- `Display`: names, ratings, PI/class, graph maxes, and precomputed stats.
- `Visual`: model/graphics/icon/thumbnail values.
- `Menu`: price, level, stock flag, ordering, unlock/availability, and similar menu metadata.
- `Derived`: read-only helper values joined/calculated by this editor.
- `Unknown`: not classified yet; treat it as unverified until a live test proves it.

The tags are conservative. They are based on schema relationships, exact DB comparisons, and observed menu behavior, not a full decompile of every game call site.

## Values that appear to drive upgrade menu effects

- Weight system summary:
  - The stock car weight baseline is `Data_Car.CurbWeight * 100`, in kg.
  - That baseline exactly matches the stock `List_UpgradeCarBodyWeight.Mass` row for the stock `CarBodyID`.
  - Upgrade weight is built from the selected upgrade rows, not from recalculating every base definition table live.
  - Most upgrade tables use `MassDiff` in kg. Add the selected row's `MassDiff` to the baseline/effective current mass.
  - Weight reduction is different: use `List_UpgradeCarBodyWeight.Mass` directly, or use `Mass - InitialMass` as its effective delta.
  - `WeightDistDiff` works the same way conceptually for front/rear distribution: it is an authored delta on the selected upgrade row.

- Engine swaps:
  - `List_UpgradeEngine.MassDiff` is the upgrade row weight effect.
  - `Data_Engine.EngineMass-kg` is the base engine definition mass.
  - Across the local DB, every non-stock engine swap row has `MassDiff = swapped Data_Engine.EngineMass-kg - stock Data_Engine.EngineMass-kg`.
  - This value is precomputed in `List_UpgradeEngine`. Editing only `Data_Engine.EngineMass-kg` does not rewrite existing `List_UpgradeEngine.MassDiff` rows, so the upgrade menu does not move.
  - To change engine swap weight in the upgrade menu, edit `List_UpgradeEngine.MassDiff` for that car/swap. If you also want the base engine definition to stay internally consistent, edit `Data_Engine.EngineMass-kg` too.
  - In the stock DB, this relationship matched 2186 non-stock engine swap rows with no meaningful mismatch above 0.05 kg.

- Car/body weight reduction:
  - `List_UpgradeCarBodyWeight.Mass` is the actual mass for that weight-reduction option.
  - `List_UpgradeCarBodyWeight.InitialMass` is the stock body mass.
  - `Mass - InitialMass` is the effective weight delta.
  - `Data_Car.CurbWeight` is stored as hundreds of kg, so `CurbWeight * 100` matches the kg scale used by `List_UpgradeCarBodyWeight`.

- Other upgrade rows that can affect weight:
  - Car-level rows keyed by `Ordinal`: engine, drivetrain, car body, motor, brakes, spring/damper, anti-sway bars, tire compound, rear wing, rim sizes.
  - Engine-part rows keyed by `EngineID`: camshaft, valves, displacement, pistons, fuel, ignition, exhaust, intake, flywheel, manifold, restrictor, oil cooling, turbos/superchargers, intercooler.
  - Drivetrain-part rows keyed by `DrivetrainID`: clutch, transmission, differential. Driveline also has `MassDiff`.
  - CarBody-part rows keyed by `CarBodyID`/`CarBodyId`: bumpers, hood, side skirts, tire widths, chassis stiffness, weight reduction.

- Tire width:
  - `List_UpgradeCarBodyTireWidthFront.FrontTireWidth` and `List_UpgradeCarBodyTireWidthRear.RearTireWidth` are direct upgrade values, which is why edits show up clearly in-game.

- Engine power/torque:
  - `Data_Engine.EngineGraphingMaxPower`, `EngineGraphingMaxTorque`, and `EngineGraphingAspirationID` are graph/display fields.
  - `List_UpgradeEngineCamshaft.TorqueCurveFullThrottleID` links to `List_TorqueCurve.TorqueCurveID`.
  - `List_TorqueCurve.TorqueScale` and the `v0...v245` values are the actual-looking torque curve data.
  - Engine part tables use selected-row scalers such as `TorqueScale`, `MaxScale`, `PowerMaxScale`, `MinScale`, `PowerMinScale`, `RobScale`, `RedlineRPM`, `StallRPM`, and boost/dropoff fields.
  - `Data_Engine.EngineMass-kg` is not part of the live upgrade weight result once `List_UpgradeEngine.MassDiff` has been authored.

- Aspiration conversions:
  - The first Aspiration screen is driven by global menu metadata, especially `UpgradeTypes`, `Upgrades`, `UpgradeAreaForUpgradeType`, and `List_Aspiration`.
  - `List_Aspiration.KeyPartName` maps the first-layer conversion choice to the actual engine part table. For example `QuadTurbo` points at `List_UpgradeEngineTurboQuad`.
  - The per-engine turbo/supercharger tables (`List_UpgradeEngineTurboSingle`, `List_UpgradeEngineTurboTwin`, `List_UpgradeEngineTurboQuad`, `List_UpgradeEngineCSC`, `List_UpgradeEngineDSC`) only provide the selected engine's actual option rows. Level 1 makes the conversion available, and higher levels become the upgrades shown after the conversion is equipped.
  - Adding rows to `List_UpgradeEngineTurboQuad` alone does not make Quad Turbo appear if the global Aspiration menu has no normal Level 4 conversion row.
  - In the local base DB, `List_UpgradeEngineTurboQuad` has zero rows, no engine has stock aspiration 4, and the `Upgrades` table has only a stock/exception Level 4 Aspiration row. The editor's `Add Conversion` action adds the mapped engine rows and wires the missing Quad Turbo menu metadata; `Wire Menu` only fixes the metadata side for the currently open table.

- Extra upgrade levels:
  - The selected part table row and the global menu row both need the same `Level`.
  - If an editor action creates `Level 4` in a table whose `UpgradeTypes` metadata only has Levels 0-3, the editor now clones the nearest existing `Upgrades` row and writes a matching global Level 4 row.
  - For duplicated part metadata such as rotary camshafts or diesel/carb fuel systems, the editor wires every `UpgradeTypes` variant with the same `PartName`.
  - Extra levels can still reuse older icon/name assets if the game DB has no real asset strings for the new level.
  - Engine power part tables appear capped by the shipped data: most engine part tables stop at Level 3, single/twin/quad turbo stop at Level 4, centrifugal/positive-displacement superchargers stop at Level 3, and no stock engine part table has Level 5. The editor can write a Level 5 row plus matching `Upgrades` metadata, but current evidence says the in-game engine upgrade UI may ignore it.

- Tire compounds:
  - `List_UpgradeTireCompound.TireModelName` is the menu label such as `Semi_Slick` or `Slick`.
  - `List_UpgradeTireCompound.TireCompoundID` links to base tire physics in `List_TireCompound.TireCompoundID`.
  - `List_TyreCurveDB.TireCompoundID` holds another set of compound curve values.

- Suspension tuning limits:
  - `List_UpgradeSpringDamper.FrontSpringDamperPhysicsID` and `RearSpringDamperPhysicsID` select rows in `List_SpringDamperPhysics`.
  - `List_SpringDamperPhysics.MinRideHeight` and `MaxRideHeight` are the editable ride-height limits.
  - The same table also holds spring and damping defaults/min/max values.

- Swaybar tuning limits:
  - `List_UpgradeAntiSwayFront/Rear.AntiSwayPhysicsID` selects rows in `List_AntiSwayPhysics`.
  - `List_AntiSwayPhysics.MinSwaybarStiffness` and `MaxSwaybarStiffness` are the editable tuning range values.

- Brakes:
  - `List_UpgradeBrakes.BrakeTorqueSlider`, `BrakeBiasSlider`, `FrontBrakeTorqueClamp`, and `RearBrakeTorqueClamp` are the obvious brake tuning/effect values.
  - `BrakesProfileID` links to `List_BrakeProfile`.

- Aero:
  - `List_UpgradeRearWing.AeroPhysicsID` and some front bumper rows link to `List_AeroPhysics`.
  - `List_AeroPhysics.Drag0/Downforce0/Drag1/Downforce1` are the aero tuning/effect endpoints.

- Garage/profile:
  - `NewProfile_Career_Garage` stores currently selected upgrade IDs and current tuning slider values.
  - The clean base DB may have no rows here; upgrade definitions live in the `List_Upgrade*` and physics tables above.

## Values that are mostly display or precomputed

- `Data_Car.PI`, `PerformanceIndex`, `ClassID`, `SpeedRating`, `HandlingRating`, `AccelerationRating`, `LaunchRating`, `BrakingRating`, and `OffroadRating` are static/precomputed display values in the DB. The game can still calculate actual behavior from parts and physics.
- `Data_Car.Sim*` columns are precomputed stat outputs, not the source physics values.
- `Data_Car.Time:*`, `TopSpeed-mph`, and quarter-mile fields are display/stat values.
- `Data_Engine.EngineGraphing*` and `Data_Motor.MotorGraphing*` are graph values, not the source torque curve.
- `MediaName`, `DisplayName`, `ModelShort`, `EngineName`, `TireModelName`, and similar name/model fields are labels or visual/menu references.

## Practical edit targets

- To change a car's stock/base weight, edit `Data_Car.CurbWeightKg` in the Core tab. The editor writes it back to raw `Data_Car.CurbWeight` as kg / 100.
- To change engine swap added/removed weight, edit `List_UpgradeEngine.MassDiff`.
- To change weight-reduction upgrade mass, edit `List_UpgradeCarBodyWeight.Mass`.
- To change tire upgrade grip/pressure behavior, follow `List_UpgradeTireCompound.TireCompoundID` into `List_TireCompound` and `List_TyreCurveDB`.
- To change ride-height/spring/damper tuning limits, follow `List_UpgradeSpringDamper.*SpringDamperPhysicsID` into `List_SpringDamperPhysics`.
- To change swaybar tuning limits, follow `List_UpgradeAntiSwayFront/Rear.AntiSwayPhysicsID` into `List_AntiSwayPhysics`.
- To change aero tuning endpoints, follow `AeroPhysicsID` into `List_AeroPhysics`.
- To change engine output shape, inspect `List_UpgradeEngineCamshaft.TorqueCurveFullThrottleID` and the linked `List_TorqueCurve` row, plus selected engine-part scalers.
