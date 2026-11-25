// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using UTF8String = eft_dma_radar.Silk.Misc.UTF8String;
namespace eft_dma_radar.Silk.Tarkov.Unity.IL2CPP
{
    public static partial class Il2CppDumper
    {
        // ── Schema ───────────────────────────────────────────────────────────────

        private enum FieldKind { Normal, MethodRva }

        private readonly struct SchemaField(string il2cpp, string cs, FieldKind kind = FieldKind.Normal)
        {
            public readonly string Il2CppName = il2cpp; // name as it appears in IL2CPP metadata
            public readonly string CsName = cs;     // name to emit in the output struct
            public readonly FieldKind Kind = kind;
        }

        private sealed class SchemaClass(string il2cpp, string cs, bool isStatic, SchemaField[] fields, uint? typeIndex, string? resolveViaChild = null)
        {
            public readonly string Il2CppName = il2cpp; // plain class name used for name-based lookup
            public readonly string CsName = cs;     // struct name in generated output
            public readonly bool IsStatic = isStatic;   // emit as static class (singleton statics)
            public readonly SchemaField[] Fields = fields;
            /// <summary>
            /// When non-null, resolves the class directly via
            ///   tablePtr + TypeIndex * 8
            /// without name-string matching. Required for obfuscated EFT classes.
            /// Obtain from Offsets.Special or by scanning the type table offline.
            /// MDToken → TypeIndex: (mdToken &amp; 0x00FFFFFF) - 1
            /// </summary>
            public readonly uint? TypeIndex = typeIndex;
            /// <summary>
            /// When non-null, resolves the class by finding this concrete child class
            /// in the type table and walking its Il2CppClass::parent chain until a
            /// class whose name matches <see cref="Il2CppName"/> is found.
            /// Required for generic type definitions (e.g. <c>BaseHealthController`1</c>)
            /// whose TypeInfoTable entry has all field offsets set to 0.
            /// The parent chain from a concrete child yields the inflated generic
            /// instance with real offsets.
            /// </summary>
            public readonly string? ResolveViaChild = resolveViaChild;
        }

        // Shorthand helpers
        private static SchemaField F(string il2cpp, string? cs = null)
            => new(il2cpp, cs ?? il2cpp, FieldKind.Normal);
        private static SchemaField M(string il2cpp, string? cs = null)
            => new(il2cpp, cs ?? (il2cpp + "_RVA"), FieldKind.MethodRva);

        /// <param name="il2cpp">Plain IL2CPP class name (only used for name-based fallback).</param>
        /// <param name="f">Fields / methods to dump.</param>
        /// <param name="cs">Output struct name (defaults to il2cpp).</param>
        /// <param name="s">Emit as static class.</param>
        /// <param name="ti">
        /// TypeIndex for direct O(1) lookup.
        /// Set this for any class whose name is obfuscated in EFT (\uXXXX).
        /// Obtain via: (MDToken &amp; 0x00FFFFFF) - 1, or from Offsets.Special.
        /// Leave 0 to use name-based lookup (only reliable for non-obfuscated classes).
        /// </param>
        /// <param name="child">
        /// Concrete child class name for resolving generic parent classes.
        /// The dumper walks <c>Il2CppClass::parent</c> from this child until it
        /// finds a class matching <paramref name="il2cpp"/>, yielding the inflated
        /// generic instance with real field offsets.
        /// </param>
        private static SchemaClass C(string il2cpp, SchemaField[] f, string? cs = null, bool s = false, uint ti = 0, string? child = null)
            => new(il2cpp, cs ?? il2cpp, s, f, ti == 0 ? null : ti, child);

        private static SchemaClass[] BuildSchema() =>
        [
            // TarkovApplication
            C("TarkovApplication", [F("_menuOperation")]),

            // MainMenuShowOperation
            C("MainMenuShowOperation", [F("_afkMonitor"), F("_preloaderUI"), F("_profile")]),

            // PreloaderUI
            C("PreloaderUI", [F("_sessionIdText"), F("_alphaVersionLabel")]),

            // AFKMonitor → AfkMonitor
            C("AFKMonitor", [F("_afkTimeout", "Delay")], cs: "AfkMonitor"),

            // GameWorld (base fields)
            C("EFT.GameWorld", [
                F("GameDateTime"),
                F("<SynchronizableObjectLogicProcessor>k__BackingField", "SynchronizableObjectLogicProcessor"),
            ], cs: "GameWorld"),

            // GameWorld → ClientLocalGameWorld (extended fields from same IL2CPP class)
            C("EFT.GameWorld", [
                F("<BtrController>k__BackingField", "BtrController"),
                F("<TransitController>k__BackingField", "TransitController"),
                F("<ExfiltrationController>k__BackingField", "ExfilController"),
                F("<ClientShellingController>k__BackingField", "ClientShellingController"),
                F("<LocationId>k__BackingField", "LocationId"),
                F("TrajectoryCalculatorPool"),
                F("LootList"),
                F("RegisteredPlayers"),
                F("BorderZones"),
                F("MainPlayer"),
                F("_world", "World"),
                F("<SynchronizableObjectLogicProcessor>k__BackingField", "SynchronizableObjectLogicProcessor"),
                F("Grenades"),
            ], cs: "ClientLocalGameWorld"),

            // TransitController
            C("TransitController", [F("pointsById", "TransitPoints")]),

            // ArtilleryShellingControllerClient → ClientShellingController
            C("ArtilleryShellingControllerClient", [F("ActiveClientProjectiles")], cs: "ClientShellingController"),

            // World_2 → WorldController
            C("World_2", [F("_interactables", "Interactables")], cs: "WorldController"),

            // WorldInteractiveObject → Interactable
            C("WorldInteractiveObject", [F("KeyId"), F("Id"), F("_doorState")], cs: "Interactable"),

            // ArtilleryProjectileClient
            C("ArtilleryProjectileClient", [F("_targetPosition", "Position"), F("_flyOn", "IsActive")]),

            // TransitPoint
            C("TransitPoint", [F("parameters")]),

            // TransitParameters
            C("TransitParameters", [F("id"), F("active"), F("name"), F("description"), F("target"), F("location")]),

            // SynchronizableObject
            C("EFT.SynchronizableObjects.SynchronizableObject", [F("Type")], cs: "SynchronizableObject"),

            // SynchronizableObjectLogicProcessor
            // NOTE: Dump from `_staticSynchronizableObjects` (the list that actually contains
            // placed tripwires in current builds). Historically this struct exposed
            // `_activeSynchronizableObjects` at the same slot; we keep the C# field name for
            // backwards compatibility but resolve the offset from the correct IL2CPP field.
            C("EFT.SynchronizableObjects.SynchronizableObjectLogicProcessor", [F("_staticSynchronizableObjects", "_activeSynchronizableObjects")], cs: "SynchronizableObjectLogicProcessor"),

            // TripwireSynchronizableObject
            C("EFT.SynchronizableObjects.TripwireSynchronizableObject", [
                F("<GrenadeTemplateId>k__BackingField", "GrenadeTemplateId"),
                F("_tripwireState"),
                F("<FromPosition>k__BackingField", "FromPosition"),
                F("<ToPosition>k__BackingField", "ToPosition"),
            ], cs: "TripwireSynchronizableObject"),

            // BorderZone (base for SniperFiringZone)
            C("BorderZone", [
                F("<Description>k__BackingField", "Description"),
            ]),

            // SniperFiringZone (AI sniper kill zones — derives from BorderZone)
            C("SniperFiringZone", [
                F("_shotFrequency"),
                F("_triggerZoneHitProbability"),
                F("_bufferZoneHitProbability"),
                F("_firstShotHitProbability"),
                F("_ammoTemplate"),
            ]),

            // Minefield (mine danger zones — derives from BorderZone)
            C("Minefield", [
                F("_collateralDamageRange"),
                F("_firstExplosionDamage"),
                F("_secondExplosionDamage"),
            ]),

            // BtrController (singleton with static <Instance>k__BackingField + instance <BtrView>)
            C("BtrController", [
                F("<Instance>k__BackingField", "_instance"),
                F("<BtrView>k__BackingField", "BtrView"),
                F("<IsBtrPaid>k__BackingField", "IsBtrPaid"),
            ], s: true, ti: Offsets.Special.BtrController_TypeIndex),

            // BTRView
            C("EFT.Vehicle.BTRView", [F("turret"), F("_previousPosition"), F("_btrState"), F("RouteState"), F("_timeToEndPause")], cs: "BTRView"),

            // BTRTurretView
            C("EFT.Vehicle.BTRTurretView", [F("_bot", "AttachedBot")], cs: "BTRTurretView"),

            // EffectsController
            C("EffectsController", [
                F("_effectsPrefab"),
                F("FastVineteFlicker"),
                F("<RainScreenDrops>k__BackingField", "RainScreenDrops"),
                F("<ScreenWater>k__BackingField", "ScreenWater"),
                F("_vignette"),
                F("_doubleVision"),
                F("_hueFocus"),
                F("_radialBlur"),
                F("_sharpen"),
                F("_lowhHealthBlend"),
                F("_bloodlossBlend"),
                F("_wiggle"),
                F("_motionBluer"),
                F("_bloodOnScreen"),
                F("_grenadeFlash"),
                F("_eyeBurn"),
                F("_blur"),
                F("_dof"),
                F("_effectAccumulators"),
                F("_sharpenAccumulator"),
                F("_radialBlurAccumulator"),
                F("_chromaticAberration"),
                F("_thermalVision"),
                F("_frostbiteEffect"),
            ]),

            // FrostbiteEffect
            C("FrostbiteEffect", [F("_opacity")]),

            // NightVision
            C("NightVision", [F("_on")]),

            // ThermalVision
            C("ThermalVision", [
                F("_material", "Material"), F("On"), F("IsNoisy"), F("IsFpsStuck"), F("IsMotionBlurred"),
                F("IsGlitch"), F("IsPixelated"), F("ChromaticAberrationThermalShift"),
                F("UnsharpRadiusBlur"), F("UnsharpBias"),
            ]),

            // BaseHealthController`1 → HealthController (Energy / Hydration pointers)
            // Generic definition has 0 offsets — resolve via concrete child's parent chain.
            C("BaseHealthController`1", [
                F("_energy", "Energy"),
                F("_hydration", "Hydration"),
            ], cs: "HealthController", child: "ClientPlayerHealthController"),

            // HealthValue (Value → ValueStruct offset)
            C("HealthValue", [F("Value")]),

            // ExfiltrationController → ExfilController
            C("CommonAssets.Scripts.Game.ExfiltrationController", [
                F("<ExfiltrationPoints>k__BackingField", "ExfiltrationPointArray"),
                F("<ScavExfiltrationPoints>k__BackingField", "ScavExfiltrationPointArray"),
                F("<SecretExfiltrationPoints>k__BackingField", "SecretExfiltrationPointArray"),
            ], cs: "ExfilController"),

            // ExfiltrationPoint → Exfil
            C("EFT.Interactive.ExfiltrationPoint", [F("_status"), F("Settings"), F("EligibleEntryPoints")], cs: "Exfil"),

            // ScavExfiltrationPoint → ScavExfil
            C("EFT.Interactive.ScavExfiltrationPoint", [F("EligibleIds")], cs: "ScavExfil"),

            // ExitTriggerSettings → ExfilSettings
            C("EFT.Interactive.ExitTriggerSettings", [F("Name")], cs: "ExfilSettings"),

            // Grenade (fields from Grenade class)
            C("Grenade", [F("<WeaponSource>k__BackingField", "WeaponSource")], cs: "Grenade"),

            // Throwable (fields from Throwable class → same output struct Grenade)
            C("Throwable", [F("_isDestroyed", "IsDestroyed")], cs: "Grenade"),

            // Player
            C("EFT.Player", [
                F("_characterController"),
                F("<MovementContext>k__BackingField", "MovementContext"),
                F("_playerBody"),
                F("<ProceduralWeaponAnimation>k__BackingField", "ProceduralWeaponAnimation"),
                F("_animators"),
                F("EnabledAnimators"),
                F("Corpse"),
                F("<Location>k__BackingField", "Location"),
                F("<InteractableObject>k__BackingField", "InteractableObject"),
                F("<Profile>k__BackingField", "Profile"),
                F("Physical"),
                F("<AIData>k__BackingField", "AIData"),
                F("_healthController"),
                F("_inventoryController"),
                F("_handsController"),
                F("_playerLookRaycastTransform"),
                F("<InteractionRayOriginOnStartOperation>k__BackingField", "InteractionRayOriginOnStartOperation"),
                F("<InteractionRayDirectionOnStartOperation>k__BackingField", "InteractionRayDirectionOnStartOperation"),
                F("<IsYourPlayer>k__BackingField", "IsYourPlayer"),
                F("<VoipID>k__BackingField", "VoipID"),
                F("<PlayerId>k__BackingField", "Id"),
                F("<GameWorld>k__BackingField", "GameWorld"),
            ], cs: "Player"),

            // ObservedPlayerView
            C("EFT.NextObservedPlayer.ObservedPlayerView", [
                F("<ObservedPlayerController>k__BackingField", "ObservedPlayerController"),
                F("<Voice>k__BackingField", "Voice"),
                F("<VisibleToCameraType>k__BackingField", "VisibleToCameraType"),
                F("<GroupId>k__BackingField", "GroupID"),
                F("<Side>k__BackingField", "Side"),
                F("<IsAI>k__BackingField", "IsAI"),
                F("<NickName>k__BackingField", "NickName"),
                F("<AccountId>k__BackingField", "AccountId"),
                F("<PlayerBody>k__BackingField", "PlayerBody"),
                F("<Id>k__BackingField", "Id"),
                F("<VoipID>k__BackingField", "VoipId"),
                F("_playerLookRaycastTransform"),
            ], cs: "ObservedPlayerView"),

            // ObservedPlayerController
            C("EFT.NextObservedPlayer.ObservedPlayerController", [
                F("<InventoryController>k__BackingField", "InventoryController"),
                F("<PlayerView>k__BackingField", "Player"),
                F("<InfoContainer>k__BackingField", "InfoContainer"),
                F("<MovementController>k__BackingField", "MovementController"),
                F("<HealthController>k__BackingField", "HealthController"),
                F("<HandsController>k__BackingField", "HandsController"),
            ], cs: "ObservedPlayerController"),

            // ObservedPlayerStateContext → ObservedMovementController
            C("EFT.NextObservedPlayer.ObservedPlayerStateContext", [
                F("<Rotation>k__BackingField", "Rotation"),
                F("_velocity", "Velocity"),
            ], cs: "ObservedMovementController"),

            // ObservedPlayerHandsController → ObservedHandsController
            C("EFT.NextObservedPlayer.ObservedPlayerHandsController", [
                F("_item", "ItemInHands"),
                F("_bundleAnimationBones", "BundleAnimationBones"),
            ], cs: "ObservedHandsController"),

            // BundleAnimationBones → BundleAnimationBonesController
            C("BundleAnimationBones", [
                F("<ProceduralWeaponAnimation>k__BackingField", "ProceduralWeaponAnimationObs"),
            ], cs: "BundleAnimationBonesController"),

            // ProceduralWeaponAnimation → ProceduralWeaponAnimationObs (observed _isAiming)
            C("EFT.Animations.ProceduralWeaponAnimation", [
                F("_isAiming", "_isAimingObs"),
            ], cs: "ProceduralWeaponAnimationObs"),

            // ObservedPlayerHealthController → ObservedHealthController
            C("ObservedPlayerHealthController", [
                F("_player", "Player"),
                F("_playerCorpse", "PlayerCorpse"),
                F("HealthStatus"),
            ], cs: "ObservedHealthController"),

            // ProceduralWeaponAnimation (main)
            C("EFT.Animations.ProceduralWeaponAnimation", [
                F("<ShotNeedsFovAdjustments>k__BackingField", "ShotNeedsFovAdjustments"),
                F("Breath"),
                F("PositionZeroSum"),
                F("Shootingg"),
                F("_aimingSpeed"),
                F("_isAiming"),
                F("_optics"),
                F("_shotDirection"),
                F("Mask"),
                F("HandsContainer"),
                F("_fovCompensatoryDistance"),
            ], cs: "ProceduralWeaponAnimation"),

            // PlayerSpring → HandsContainer
            C("EFT.Animations.PlayerSpring", [
                F("CameraOffset"),
                F("HandsRotation"),
                F("CameraRotation"),
                F("CameraPosition"),
            ], cs: "HandsContainer"),

            // SightNBone
            C("SightNBone", [F("Mod")]),

            // ShotEffector
            C("ShotEffector", [F("NewShotRecoil")]),

            // PlayerStateContainer
            C("PlayerStateContainer", [F("Name"), F("StateFullNameHash")]),

            // NewRecoilShotEffect → NewShotRecoil
            C("NewRecoilShotEffect", [F("IntensitySeparateFactors")], cs: "NewShotRecoil"),

            // VisorEffect
            C("VisorEffect", [F("Intensity")]),

            // TOD_Time
            C("TOD_Time", [F("LockCurrentTime")]),

            // TOD_CycleParameters
            C("TOD_CycleParameters", [F("Hour")]),

            // TOD_ImageEffect → TOD_Scattering
            C("TOD_ImageEffect", [F("_sky", "Sky")], cs: "TOD_Scattering"),

            // TOD_Sky
            C("TOD_Sky", [
                F("<Cycle>k__BackingField", "Cycle"),
                F("<Components>k__BackingField", "TOD_Components"),
            ]),

            // TOD_Components
            C("TOD_Components", [F("<Time>k__BackingField", "TOD_Time")]),

            // Profile
            C("EFT.Profile", [
                F("Id"), F("AccountId"), F("Info"), F("Inventory"), F("Skills"),
                F("TaskConditionCounters"), F("QuestsData"), F("WishlistManager"), F("Stats"),
            ], cs: "Profile"),

            // WishlistManager
            C("EFT.WishlistManager", [F("_userItems", "Items")], cs: "WishlistManager"),

            // ProfileInfo → PlayerInfo
            C("EFT.ProfileInfo", [
                F("Nickname"), F("EntryPoint"), F("<Side>k__BackingField", "Side"), F("RegistrationDate"),
                F("GroupId"), F("<Settings>k__BackingField", "Settings"), F("MemberCategory"), F("_experience", "Experience"),
            ], cs: "PlayerInfo"),

            // SkillManager
            C("SkillManager", [
                F("StrengthBuffJumpHeightInc"), F("StrengthBuffThrowDistanceInc"),
                F("MagDrillsLoadSpeed"), F("MagDrillsUnloadSpeed"),
                F("RaidLoadedAmmoAction"), F("RaidUnloadedAmmoAction"),
            ]),

            // FloatBuff → SkillValueContainer
            C("FloatBuff", [F("Value")], cs: "SkillValueContainer"),

            // QuestStatusData → QuestData
            C("EFT.Quests.QuestStatusData", [F("Id"), F("Status"), F("CompletedConditions"), F("Template")], cs: "QuestData"),

            // CompletedConditionsCollection
            C("CompletedConditionsCollection", [
                F("_backendData", "BackendData"),
                F("_localChanges", "LocalChanges"),
            ]),

            // QuestTemplate
            C("QuestTemplate", [
                F("<Conditions>k__BackingField", "Conditions"),
                F("_questName", "Name"),
            ]),

            // ItemHandsController
            C("ItemHandsController", [F("_item", "Item")]),

            // FirearmController (+ get_WeaponDirection method RVA for the silent-aim code-patch)
            C("FirearmController", [F("Fireport"), F("COI", "TotalCenterOfImpact"), F("WeaponLn"), M("get_WeaponDirection")]),

            // ClientFirearmController (fields from ClientFirearmController + inherited FirearmController)
            C("FirearmController", [F("WeaponLn")], cs: "ClientFirearmController"),
            C("ClientFirearmController", [F("LastShotId", "ShotIndex")], cs: "ClientFirearmController"),

            // MovementContext
            C("EFT.MovementContext", [
                F("_player", "Player"),
                F("_rotation"),
                F("PlantState"),
                F("<CurrentState>k__BackingField", "CurrentState"),
                F("_states"),
                F("_movementStates"),
                F("_tilt"),
                F("_physicalCondition"),
                F("_speedLimitIsDirty"),
                F("<StateSpeedLimit>k__BackingField", "StateSpeedLimit"),
                F("<StateSprintSpeedLimit>k__BackingField", "StateSprintSpeedLimit"),
                F("_lookDirection"),
                F("<WalkInertia>k__BackingField", "WalkInertia"),
                F("<SprintBrakeInertia>k__BackingField", "SprintBrakeInertia"),
                F("_poseInertia"),
                F("_currentPoseInertia"),
                F("_inertiaAppliedTime"),
            ], cs: "MovementContext"),

            // MovementState (from MovementState class)
            C("MovementState", [F("StickToGround"), F("PlantTime")], cs: "MovementState"),

            // BaseMovementState (from BaseMovementState class → same output)
            C("BaseMovementState", [F("Name"), F("AnimatorStateHash"), F("AuthoritySpeed")], cs: "MovementState"),

            // MovePlayerState (from MovePlayerState class → same output)
            C("MovePlayerState", [F("_velocity"), F("_velocity2")], cs: "MovementState"),

            // InventoryController
            C("EFT.InventoryLogic.InventoryController", [F("<Inventory>k__BackingField", "Inventory")], cs: "InventoryController"),

            // Inventory
            C("EFT.InventoryLogic.Inventory", [F("Equipment"), F("QuestRaidItems"), F("QuestStashItems"), F("Stash")], cs: "Inventory"),

            // Stash — StashClass derives from CompoundItem, so its grid array is the
            // inherited CompoundItem.Grids field (exactly like Equipment below), NOT a
            // "_grid" field. Resolving "_grid" produced a valid-but-wrong offset, so the
            // offline stash chain read 0 items even though the stash object itself
            // resolved fine (the independent area/upgrade chain was unaffected).
            C("EFT.InventoryLogic.CompoundItem", [F("Grids"), F("Slots")], cs: "Stash"),

            // CompoundItem → Equipment
            C("EFT.InventoryLogic.CompoundItem", [F("Grids"), F("Slots")], cs: "Equipment"),

            // BarterOther → BarterOtherOffsets
            C("EFT.InventoryLogic.BarterOther", [F("Dogtag")], cs: "BarterOtherOffsets"),

            // DogtagComponent
            C("EFT.InventoryLogic.DogtagComponent", [
                F("GroupId"), F("AccountId"), F("ProfileId"), F("Nickname"),
                F("Side"), F("Level"), F("Time"), F("Status"), F("KillerAccountId"),
                F("KillerProfileId"), F("KillerName"), F("WeaponName"), F("CarriedByGroupMember"),
            ], cs: "DogtagComponent"),

            // Grid → Grids
            C("Grid", [F("<ItemCollection>k__BackingField", "ContainedItems")], cs: "Grids"),

            // GridItemCollection → GridContainedItems
            C("GridItemCollection", [F("ItemsList", "Items")], cs: "GridContainedItems"),

            // Slot
            C("EFT.InventoryLogic.Slot", [
                F("<ContainedItem>k__BackingField", "ContainedItem"),
                F("<ID>k__BackingField", "ID"),
                F("Required"),
            ], cs: "Slot"),

            // LootItem → InteractiveLootItem
            C("EFT.Interactive.LootItem", [F("_item", "Item")], cs: "InteractiveLootItem"),

            // Skeleton → DizSkinningSkeleton
            C("Skeleton", [F("_values")], cs: "DizSkinningSkeleton"),

            // LootableContainer (fields from LootableContainer class)
            C("EFT.Interactive.LootableContainer", [F("ItemOwner"), F("Template")], cs: "LootableContainer"),

            // WorldInteractiveObject (fields inherited → same output LootableContainer)
            C("WorldInteractiveObject", [
                F("<InteractingPlayer>k__BackingField", "InteractingPlayer"),
            ], cs: "LootableContainer"),

            // ItemController → LootableContainerItemOwner
            C("EFT.InventoryLogic.ItemController", [F("<RootItem>k__BackingField", "RootItem")], cs: "LootableContainerItemOwner"),

            // Item → LootItem
            C("EFT.InventoryLogic.Item", [
                F("StackObjectsCount"), F("Version"), F("Components"), F("<Template>k__BackingField", "Template"), F("<SpawnedInSession>k__BackingField", "SpawnedInSession"),
            ], cs: "LootItem"),

            // CompoundItem → LootItemMod
            C("EFT.InventoryLogic.CompoundItem", [F("Grids"), F("Slots")], cs: "LootItemMod"),

            // Grid → Grid
            C("Grid", [F("<ItemCollection>k__BackingField", "ItemCollection")], cs: "Grid"),

            // GridItemCollection → GridItemCollection
            C("GridItemCollection", [F("ItemsList")], cs: "GridItemCollection"),

            // Weapon → LootItemWeapon
            C("EFT.InventoryLogic.Weapon", [
                F("FireMode"),
                F("<Chambers>k__BackingField", "Chambers"),
                F("_magSlotCache"),
            ], cs: "LootItemWeapon"),

            // AmmoTemplate → AmmoTemplate (silent-aim ballistics: muzzle velocity + G1 drag inputs)
            C("EFT.InventoryLogic.AmmoTemplate", [
                F("InitialSpeed"),
                F("BallisticCoeficient"),
                F("BulletMassGram"),
                F("BulletDiameterMilimeters"),
            ], cs: "AmmoTemplate"),

            // Weapon/attachment muzzle-velocity % modifiers (silent-aim ballistics refinement)
            C("EFT.InventoryLogic.WeaponTemplate", [F("Velocity")], cs: "WeaponTemplate"),
            C("EFT.InventoryLogic.ModTemplate", [F("Velocity")], cs: "ModTemplate"),

            // LevelSettings
            C("LevelSettings", [F("AmbientMode"), F("EquatorColor"), F("GroundColor")]),

            // SlotView_2 → PlayerBodySubclass
            C("SlotView_2", [F("Dresses")], cs: "PlayerBodySubclass"),

            // Dress
            C("Dress", [F("Renderers")]),

            // EFTHardSettings (singleton with TypeIndex)
            C("EFTHardSettings", [
                F("POSE_CHANGING_SPEED"),
                F("_instance"),
                F("MED_EFFECT_USING_PANEL"),
                F("MOUSE_LOOK_HORIZONTAL_LIMIT"),
                F("MOUSE_LOOK_LIMIT_IN_AIMING_COEF"),
                F("MOUSE_LOOK_VERTICAL_LIMIT"),
                F("ABOVE_OR_BELOW"),
                F("ABOVE_OR_BELOW_STAIRS"),
                F("AIM_PROCEDURAL_INTENSITY"),
                F("AIR_CONTROL_BACK_DIR"),
                F("AIR_CONTROL_NONE_OR_ORT_DIR"),
                F("AIR_CONTROL_SAME_DIR"),
                F("AIR_LERP"),
                F("AIR_MIN_SPEED"),
                F("DecelerationSpeed"),
                F("WEAPON_OCCLUSION_LAYERS"),
                F("DOOR_RAYCAST_DISTANCE"),
                F("LOOT_RAYCAST_DISTANCE"),
            ], s: true, ti: Offsets.Special.EFTHardSettings_TypeIndex),

            // GPUInstancerManager (singleton with TypeIndex)
            C("GPUInstancerManager", [
                F("runtimeDataList"),
            ], s: true, ti: Offsets.Special.GPUInstancerManager_TypeIndex),

            // ClientBackendSession
            C("ClientBackendSession", [F("<BackEndConfig>k__BackingField", "BackEndConfig")]),

            // FireModeComponent
            C("FireModeComponent", [F("FireMode")]),

            // MagazineTemplate → LootItemMagazine
            C("MagazineTemplate", [F("Cartridges"), F("LoadUnloadModifier")], cs: "LootItemMagazine"),

            // Item → MagazineClass
            C("EFT.InventoryLogic.Item", [F("StackObjectsCount")], cs: "MagazineClass"),

            // StackSlot
            C("EFT.InventoryLogic.StackSlot", [F("_items"), F("MaxCount")], cs: "StackSlot"),

            // ItemTemplate
            C("EFT.InventoryLogic.ItemTemplate", [F("Name"), F("ShortName"), F("<_id>k__BackingField", "_id"), F("Weight"), F("QuestItem")], cs: "ItemTemplate"),

            // PlayerBody
            C("PlayerBody", [
                F("SkeletonRootJoint"), F("BodySkins"), F("_bodyRenderers"), F("SlotViews"), F("PointOfView"),
            ]),

            // InventoryBlur
            C("InventoryBlur", [F("_blurCount"), F("_upsampleTexDimension")]),

            // Physical
            C("PhysicalBase", [
                F("Overweight"), F("WalkOverweight"), F("WalkSpeedLimit"), F("Inertia"),
                F("Stamina"), F("Oxygen"), F("BaseOverweightLimits"), F("SprintOverweightLimits"),
                F("PreviousWeight"), F("SprintAcceleration"), F("PreSprintAcceleration"),
                F("_encumbered"), F("_overEncumbered"), F("SprintOverweight"), F("<BerserkRestorationFactor>k__BackingField", "BerserkRestorationFactor"),
            ], cs: "Physical"),

            // Stamina → PhysicalValue
            C("Stamina", [F("Current")], cs: "PhysicalValue"),

            // BreathEffector
            C("BreathEffector", [F("Intensity")]),

            // OpticCameraManager
            // The live optic manager is an OpticCameraManagerRs (runtime dump: <Camera> @ 0x90,
            // <CurrentOpticSight> @ 0x98) — EFT.CameraControl.OpticCameraManager is not what
            // CameraManagerRs._opticCameraManager points at.
            C("EFT.CameraControl.OpticCameraManagerRs", [F("<Camera>k__BackingField", "Camera"), F("<CurrentOpticSight>k__BackingField", "CurrentOpticSight")], cs: "OpticCameraManager"),

            // GPUInstancerRuntimeData
            C("GPUInstancerRuntimeData", [F("instanceBounds")]),

            // CameraManager → EFTCameraManager. The static Instance lives on CameraManager, but
            // on the Unity 6 build the instance is an EFT.CameraControl.CameraManagerRs (runtime
            // dump: _opticCameraManager @ 0x80, <Camera>k__BackingField @ 0xE0), so the instance
            // fields are taken from that class.
            C("EFT.CameraControl.CameraManager", [
                M("get_Instance", "GetInstance_RVA"),
            ], cs: "EFTCameraManager"),
            C("EFT.CameraControl.CameraManagerRs", [
                F("_opticCameraManager", "OpticCameraManager"),
                F("<Camera>k__BackingField", "Camera"),
            ], cs: "EFTCameraManager"),

            // SightComponent
            C("EFT.InventoryLogic.SightComponent", [
                F("_template"), F("ScopesSelectedModes"), F("SelectedScope"), F("ScopeZoomValue"),
            ], cs: "SightComponent"),

            // SightModTemplate → SightInterface
            C("EFT.InventoryLogic.SightModTemplate", [F("Zooms")], cs: "SightInterface"),

            // WeatherController (instance fields + static Instance, with TypeIndex)
            C("WeatherController", [F("Instance"), F("WeatherDebug")], s: true, ti: Offsets.Special.WeatherController_TypeIndex),

            // WeatherDebug
            C("WeatherDebug", [
                F("CloudDensity"), F("Fog"), F("LightningThunderProbability"),
                F("Rain"), F("WindMagnitude"), F("isEnabled"),
            ]),

            // MatchingProgress — pure data model, no static singleton
            C("MatchingProgress", [
                F("StatusUpdateEvent"),
                F("MatchingProgressChangedEvent"),
                F("<CurrentStage>k__BackingField",                     "CurrentStage"),
                F("<CurrentStageGroup>k__BackingField",                "CurrentStageGroup"),
                F("<CurrentStageProgress>k__BackingField",             "CurrentStageProgress"),
                F("<EstimateTime>k__BackingField",                     "EstimateTime"),
                F("<StartTime>k__BackingField",                        "StartTime"),
                F("<IsAbortAvailable>k__BackingField",                 "IsAbortAvailable"),
                F("<BlockAbortAbilityDurationSeconds>k__BackingField", "BlockAbortAbilityDurationSeconds"),
                F("<ShowAbortConfirmationPopup>k__BackingField",       "ShowAbortConfirmationPopup"),
                F("<IsMatchingAbortRequested>k__BackingField",         "IsMatchingAbortRequested"),
                F("<CanProcessServerStages>k__BackingField",           "CanProcessServerStages"),
            ]),

            // MatchingProgressView — MonoBehaviour owner, resolved via GOM klass-pointer walk
            C("MatchingProgressView", [
                F("_matchingProgress"),
            ], ti: Offsets.Special.MatchingProgressView_TypeIndex),

            // GamePlayerOwner (resolved via TypeIndex — singleton statics)
            C("GamePlayerOwner", [F("_myPlayer")], s: true, ti: Offsets.Special.GamePlayerOwner_TypeIndex),

            // BTRGlobalSettings (BTR map list + taxi pricing + per-map config dictionary)
            C("BTRGlobalSettings", [
                F("LocationsWithBTR"),
                F("BasePriceTaxi"),
                F("AddPriceTaxi"),
                F("CleanUpPrice"),
                F("DeliveryPrice"),
                F("ModDeliveryCost"),
                F("BearPriceMod"),
                F("UsecPriceMod"),
                F("ScavPriceMod"),
                F("CoefficientDiscountCharisma"),
                F("TaxiMinPrice"),
                F("DeliveryMinPrice"),
                F("BotCoverMinPrice"),
                F("MapsConfigs"),
            ]),

            // BTRMapPath (per-map BTR path info — value type entries in MapsConfigs)
            C("BTRMapPath", [
                F("mapID"),
                F("pathsConfigurations"),
            ]),

            // MapPathConfig (BTR route stops + spline edges + depot)
            C("MapPathConfig", [
                F("PathSplines"),
                F("PathDestinations"),
                F("DepotPosition"),
            ], cs: "MapPathConfig"),

            // AirdropManager (abstract base + ClientAirdropManager derived fields)
            C("AirdropManager", [
                F("_deactivatedAirdropPoints"),
                F("_airdropPoints"),
                F("_spawnAirdropTimer"),
                F("CachedAirdropParameters"),
                F("AirdropParameters"),
                F("_isInited"),
            ]),
            C("ClientAirdropManager", [
                F("_nextAirdropTimeRemaining"),
                F("_nextAirdropFlareRemaining"),
            ], cs: "AirdropManager"),

            // AirdropParameters (LocationSettings.Location.AirdropParameters)
            C("AirdropParameters", [
                F("PlaneAirdropStartMin"),
                F("PlaneAirdropStartMax"),
                F("PlaneAirdropEnd"),
                F("PlaneAirdropChance"),
                F("PlaneAirdropMax"),
                F("PlaneAirdropCooldownMin"),
                F("PlaneAirdropCooldownMax"),
                F("AirdropPointDeactivateDistance"),
                F("MinPlayersCountToSpawnAirdrop"),
                F("UnsuccessfulTryPenalty"),
            ]),

            // ObservedPlayerInfoContainer (NextObservedPlayer info backing fields)
            C("ObservedPlayerInfoContainer", [
                F("<IsHeavyBreathing>k__BackingField",   "IsHeavyBreathing"),
                F("<BreathIsAudible>k__BackingField",    "BreathIsAudible"),
                F("<CovertMovementSpeed>k__BackingField","CovertMovementSpeed"),
            ]),

            // GrenadeHandsController (ClientGrenadeHandsController inherits BaseGrenadeHandsController)
            C("ClientGrenadeHandsController", [
                F("GrenadePacket"),
            ], cs: "GrenadeHandsController"),
            C("GrenadePacket", [
                F("HighThrow"),
                F("LowThrow"),
                F("GrenadeThrowData"),
            ]),
            C("GrenadeThrowData", [
                F("HasThrowData"),
                F("ThrowGrenadeRotation"),
                F("ThrowGrenadePosition"),
                F("ThrowForce"),
                F("LowThrow"),
            ]),

            // TaskConditionCounter (quest condition counter value)
            C("TaskConditionCounter", [
                F("Value"),
            ]),
        ];
    }
}
