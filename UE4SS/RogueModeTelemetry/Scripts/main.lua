print("[RogueModeTelemetry] Loading combat tracker telemetry v0.9.0 RC12.4d SERVER-TRAVEL STABILITY\n")

-- Production defaults: preserve the state used by combat attribution while
-- suppressing development-only records and high-frequency console noise.
local ENABLE_VERBOSE_DIAGNOSTIC_RECORDS = false
local ENABLE_VERBOSE_CONSOLE = false

local function verbose_print(message)
    if ENABLE_VERBOSE_CONSOLE then
        print(message)
    end
end

local function resolve_output_file()
    local fallback = "RogueModeTelemetry.txt"
    local source_path = nil

    local ok, info = pcall(function()
        return debug.getinfo(1, "S")
    end)

    if ok and type(info) == "table" then
        source_path = info.source or info.short_src
    end

    if type(source_path) ~= "string" or source_path == "" then
        return fallback
    end

    -- UE4SS loads this file from:
    --   <Palworld>/Pal/Binaries/Win64/ue4ss/Mods/<mod>/scripts/main.lua
    -- Derive the shared ue4ss directory so the telemetry path works on any
    -- drive, Steam library, or Palworld installation folder.
    source_path = source_path:gsub("^@", ""):gsub("\\", "/")

    local lower_path = source_path:lower()
    local _, ue4ss_end = lower_path:find("/ue4ss/", 1, true)

    if ue4ss_end == nil and lower_path:sub(1, 6) == "ue4ss/" then
        ue4ss_end = 6
    end

    if ue4ss_end ~= nil then
        return source_path:sub(1, ue4ss_end)
            .. "RogueModeTelemetry.txt"
    end

    -- Compatibility fallback for a relative script source such as
    -- Mods/RogueModeTelemetry/scripts/main.lua.
    local script_directory = source_path:match("^(.*)/[^/]+$")

    if script_directory ~= nil and script_directory ~= "" then
        return script_directory
            .. "/../../../RogueModeTelemetry.txt"
    end

    return fallback
end

local OUTPUT_FILE = resolve_output_file()

print(string.format(
    "[RogueModeTelemetry] Telemetry output: %s\n",
    OUTPUT_FILE
))

local defender_by_component = {}
local emitted_death_by_actor = {}
local emitted_owner_by_pal_actor = {}
local owner_retry_after_by_pal_actor = {}
local emitted_name_by_actor = {}
local name_retry_after_by_actor = {}
local pal_ui_utility = nil
local pal_utility = nil
local cached_local_player_actor_name = nil
local cached_local_player_display_name = nil
local cached_local_player_full_name = nil
local next_local_player_emit_seconds = 0.0
local damage_metadata_sequence = 0
local resolved_waza_name_by_id = {}
local action_lifecycle_sequence = 0
local weapon_probe_sequence = 0
local bullet_item_by_owner_actor = {}
local active_action_by_actor = {}
local recent_weapon_by_owner_actor = {}
local emitted_weapon_identity_by_key = {}
local source_correlation_sequence = 0
local weapon_identity_sequence = 0
local resolved_item_name_by_static_id = {}
local status_event_sequence = 0
local slip_damage_sequence = 0
local replicated_status_sequence = 0
local status_generation_record_sequence = 0
local next_status_generation_by_key = {}
local emitted_status_conflict_by_key = {}
local active_status_by_defender = {}
local active_status_ids_by_component = {}
local last_status_snapshot_time_by_object = {}
local first_seen_status_object = {}
local last_status_by_defender = {}
local last_friendly_source_by_defender = {}
local friendly_source_by_instance_id = {}
local actor_instance_details_by_actor = {}
local partner_skill_sequence = 0
local partner_buff_sequence = 0
local partner_bonus_sequence = 0
local partner_skill_by_internal_name = {}
local active_partner_skill_by_player_actor = {}
local partner_skills_by_player_actor = {}
local seen_partner_status_object = {}
local last_partner_status_emit_by_object = {}
local last_primary_player_hit_by_actor = {}
local resolved_partner_skill_name_by_character_id = {}
local partner_bootstrap_sequence = 0
local partner_name_probe_sequence = 0
local last_partner_bootstrap_time_by_player = {}
local partner_buff_seen_by_player = {}
local emitted_partner_name_probe_by_key = {}
local known_pal_name_by_species_key = {}
local raid_pal_name_by_actor = {}

-- Actors damaged by the local player or active Pal. Raid bosses can be
-- removed from the world without firing the normal PalCharacter death event,
-- so destruction/end-play hooks use this set as a filtered fallback.
local friendly_damaged_defender_by_actor = {}

local function get_value(parameter)
    if parameter == nil then
        return nil
    end

    local ok, value = pcall(function()
        return parameter:get()
    end)

    if ok then
        return value
    end

    return nil
end

local function unwrap_maybe_parameter(value)
    if value == nil then
        return nil
    end

    local ok, unwrapped = pcall(function()
        return value:get()
    end)

    if ok and unwrapped ~= nil then
        return unwrapped
    end

    return value
end

local function object_name(object)
    object = unwrap_maybe_parameter(object)

    if object == nil then
        return "nil"
    end

    local ok, result = pcall(function()
        if object:IsValid() then
            return object:GetFullName()
        end

        return "invalid"
    end)

    if ok then
        return result
    end

    return "unknown"
end

local function clean_name(full_name)
    if full_name == nil then
        return "unknown"
    end

    local short_name = full_name:match("PersistentLevel%.([^%s%.]+)")

    if short_name ~= nil then
        return short_name
    end

    return full_name:gsub("|", "_")
end

local function unreal_string_to_lua(value)
    value = unwrap_maybe_parameter(value)

    if value == nil then
        return nil
    end

    if type(value) == "string" then
        return value
    end

    local ok, converted = pcall(function()
        return value:ToString()
    end)

    if ok and type(converted) == "string" then
        return converted
    end

    return tostring(value)
end

local function sanitize_field(value)
    if value == nil then
        return "unknown"
    end

    local text = unreal_string_to_lua(value) or "unknown"
    text = text:gsub("[|\r\n]", "_")

    if text == "" then
        return "unknown"
    end

    return text
end

local function is_valid_object(object)
    object = unwrap_maybe_parameter(object)

    if object == nil then
        return false
    end

    local ok, valid = pcall(function()
        return object:IsValid()
    end)

    return ok and valid
end

local function get_player_state(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return nil
    end

    local ok, player_state = pcall(function()
        return actor.PlayerState
    end)

    player_state = unwrap_maybe_parameter(player_state)

    if ok and is_valid_object(player_state) then
        return player_state
    end

    local controller_ok, controller = pcall(function()
        return actor:GetController()
    end)

    controller = unwrap_maybe_parameter(controller)

    if not controller_ok or not is_valid_object(controller) then
        return nil
    end

    local state_ok, controller_state = pcall(function()
        return controller.PlayerState
    end)

    controller_state = unwrap_maybe_parameter(controller_state)

    if state_ok and is_valid_object(controller_state) then
        return controller_state
    end

    return nil
end

local function get_player_display_name(actor)
    local player_state = get_player_state(actor)

    if player_state == nil then
        return nil
    end

    local ok, player_name = pcall(function()
        return player_state:GetPlayerName()
    end)

    player_name = unwrap_maybe_parameter(player_name)

    if ok and player_name ~= nil then
        local text = sanitize_field(player_name)

        if text ~= "unknown" and text ~= "None" then
            return text
        end
    end

    local property_ok, private_name = pcall(function()
        return player_state.PlayerNamePrivate
    end)

    private_name = unwrap_maybe_parameter(private_name)

    if property_ok and private_name ~= nil then
        local text = sanitize_field(private_name)

        if text ~= "unknown" and text ~= "None" then
            return text
        end
    end

    return nil
end

local function get_character_parameter_component(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return nil
    end

    local property_ok, component = pcall(function()
        return actor.CharacterParameterComponent
    end)

    component = unwrap_maybe_parameter(component)

    if property_ok and is_valid_object(component) then
        return component
    end

    local function_ok, function_component = pcall(function()
        return actor:GetCharacterParameterComponent()
    end)

    function_component = unwrap_maybe_parameter(function_component)

    if function_ok and is_valid_object(function_component) then
        return function_component
    end

    return nil
end

local function is_players_otomo(actor)
    local component = get_character_parameter_component(actor)

    if component == nil then
        return false
    end

    local ok, result = pcall(function()
        return component:IsPlayersOtomo()
    end)

    return ok and result == true
end

local function get_active_actor_flag(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return false
    end

    local ok, result = pcall(function()
        return actor:GetActiveActorFlag()
    end)

    if ok then
        return result == true
    end

    local property_ok, property_value = pcall(function()
        return actor.bIsPalActiveActor
    end)

    return property_ok and property_value == true
end

local NAME_PRIORITY_FALLBACK = 1
local NAME_PRIORITY_OFFICIAL = 2
local NAME_PRIORITY_NICKNAME = 3

local function meaningful_name(value)
    local text = sanitize_field(value)

    if text == nil or
        text == "" or
        text == "None" or
        text == "none" or
        text == "unknown" or
        text == "invalid" or
        text == "nil" then
        return nil
    end

    return text
end

local function normalized_name_key(value)
    local text = meaningful_name(value)

    if text == nil then
        return ""
    end

    return text:lower():gsub("[^%w]", "")
end

local function actor_species_key(actor_name)
    local value = clean_name(actor_name)

    value = value:gsub("^BP_", "")
    value = value:gsub("_C_%d+$", "")
    value = value:gsub("_C$", "")
    value = value:gsub("_BOSS$", "")
    value = value:gsub("_RAID$", "")

    return normalized_name_key(value)
end

local function remember_known_pal_species(
    actor_name,
    display_name
)
    local key = actor_species_key(actor_name)
    local resolved_name = meaningful_name(display_name)

    if key == "" or resolved_name == nil then
        return
    end

    local existing = known_pal_name_by_species_key[key]
    local existing_is_raw =
        existing == nil or
        tostring(existing):find("BP_", 1, true) ~= nil
    local incoming_is_raw =
        tostring(resolved_name):find("BP_", 1, true) ~= nil

    if existing == nil or
        (existing_is_raw and not incoming_is_raw) then
        known_pal_name_by_species_key[key] = resolved_name
    end
end

local function get_known_pal_species_name(actor_name)
    local key = actor_species_key(actor_name)

    if key == "" then
        return nil
    end

    return known_pal_name_by_species_key[key]
end

local function remember_raid_pal_actor(
    actor_name,
    display_name
)
    actor_name = clean_name(actor_name)

    if actor_name == nil or
        actor_name == "" or
        actor_name == "unknown" or
        actor_name == "invalid" or
        actor_name == "nil" then
        return
    end

    local resolved_name =
        meaningful_name(display_name) or
        get_known_pal_species_name(actor_name) or
        actor_name

    raid_pal_name_by_actor[actor_name] = resolved_name

    remember_known_pal_species(
        actor_name,
        resolved_name
    )
end

local function get_pal_ui_utility()
    if is_valid_object(pal_ui_utility) then
        return pal_ui_utility
    end

    local ok, utility = pcall(function()
        return StaticFindObject("/Script/Pal.Default__PalUIUtility")
    end)

    utility = unwrap_maybe_parameter(utility)

    if ok and is_valid_object(utility) then
        pal_ui_utility = utility
        return utility
    end

    return nil
end

local function get_pal_identity(component)
    local individual = nil
    local character_id = nil
    local unique_npc_id = nil
    local save_parameter = nil

    if component == nil then
        return individual, character_id, unique_npc_id, save_parameter
    end

    local individual_ok, individual_value = pcall(function()
        return component:GetIndividualParameter()
    end)

    individual_value = unwrap_maybe_parameter(individual_value)

    if individual_ok and is_valid_object(individual_value) then
        individual = individual_value

        local id_ok, id_value = pcall(function()
            return individual:GetCharacterID()
        end)

        id_value = unwrap_maybe_parameter(id_value)

        if id_ok and id_value ~= nil then
            character_id = id_value
        end

        local save_ok, save_value = pcall(function()
            return individual.SaveParameter
        end)

        save_value = unwrap_maybe_parameter(save_value)

        if save_ok and save_value ~= nil then
            save_parameter = save_value

            local unique_ok, unique_value = pcall(function()
                return save_parameter.UniqueNPCID
            end)

            unique_value = unwrap_maybe_parameter(unique_value)

            if unique_ok and unique_value ~= nil then
                unique_npc_id = unique_value
            end
        end

        if unique_npc_id == nil then
            local unique_ok, unique_value = pcall(function()
                return individual:GetUniqueNPCID()
            end)

            unique_value = unwrap_maybe_parameter(unique_value)

            if unique_ok and unique_value ~= nil then
                unique_npc_id = unique_value
            end
        end
    end

    if character_id == nil then
        local id_ok, id_value = pcall(function()
            return component:GetCharacterID()
        end)

        id_value = unwrap_maybe_parameter(id_value)

        if id_ok and id_value ~= nil then
            character_id = id_value
        end
    end

    if unique_npc_id == nil then
        local unique_ok, unique_value = pcall(function()
            return component:GetUniqueNPCID()
        end)

        unique_value = unwrap_maybe_parameter(unique_value)

        if unique_ok and unique_value ~= nil then
            unique_npc_id = unique_value
        end
    end

    if unique_npc_id == nil then
        local none_ok, none_value = pcall(function()
            return FName("None")
        end)

        if none_ok then
            unique_npc_id = none_value
        end
    end

    return individual, character_id, unique_npc_id, save_parameter
end

local function try_get_official_pal_name(
    actor,
    character_id,
    unique_npc_id
)
    if character_id == nil then
        return nil
    end

    local utility = get_pal_ui_utility()

    if utility == nil then
        return nil
    end

    -- UE4SS marshals reflected out parameters into a Lua table. The field
    -- name matches the Unreal parameter name: OutNickName.
    local out_parameters = {}

    local call_ok, return_value = pcall(function()
        return utility:GetDisplayNickName(
            actor,
            character_id,
            unique_npc_id,
            out_parameters
        )
    end)

    if call_ok then
        local out_name = meaningful_name(
            out_parameters.OutNickName or
            out_parameters.outNickName or
            out_parameters[1]
        )

        if out_name ~= nil then
            return out_name
        end

        -- Retain this as a compatibility fallback in case a different UE4SS
        -- build returns the FString directly instead of populating the table.
        local returned_name = meaningful_name(return_value)

        if returned_name ~= nil then
            return returned_name
        end
    end

    return nil
end

local function get_pal_name_info(actor, actor_name)
    actor = unwrap_maybe_parameter(actor)

    local fallback_actor_name = clean_name(actor_name)
    local component = get_character_parameter_component(actor)

    if component == nil then
        return fallback_actor_name, NAME_PRIORITY_FALLBACK
    end

    local individual, character_id, unique_npc_id, save_parameter =
        get_pal_identity(component)

    local character_id_text = meaningful_name(character_id)
    local character_key = normalized_name_key(character_id_text)
    local actor_key = actor_species_key(actor_name)

    local nickname_candidates = {}

    local component_nickname_ok, component_nickname = pcall(function()
        return component:GetNickname()
    end)

    if component_nickname_ok then
        nickname_candidates[#nickname_candidates + 1] =
            unwrap_maybe_parameter(component_nickname)
    end

    if save_parameter ~= nil then
        local saved_nickname_ok, saved_nickname = pcall(function()
            return save_parameter.NickName
        end)

        if saved_nickname_ok then
            nickname_candidates[#nickname_candidates + 1] =
                unwrap_maybe_parameter(saved_nickname)
        end
    end

    if individual ~= nil then
        local individual_nickname_ok, individual_nickname = pcall(function()
            return individual:GetNickname()
        end)

        if individual_nickname_ok then
            nickname_candidates[#nickname_candidates + 1] =
                unwrap_maybe_parameter(individual_nickname)
        end
    end

    -- A true custom nickname wins. Palworld can return the raw CharacterID
    -- from nickname accessors when no nickname was entered, so raw IDs are
    -- deliberately not treated as nicknames.
    for _, candidate in ipairs(nickname_candidates) do
        local nickname = meaningful_name(candidate)

        if nickname ~= nil then
            local nickname_key = normalized_name_key(nickname)
            local is_raw_default =
                (character_key ~= "" and nickname_key == character_key) or
                (actor_key ~= "" and nickname_key == actor_key)

            if not is_raw_default then
                return nickname, NAME_PRIORITY_NICKNAME
            end
        end
    end

    local official_name = try_get_official_pal_name(
        actor,
        character_id,
        unique_npc_id
    )

    if official_name ~= nil then
        return official_name, NAME_PRIORITY_OFFICIAL
    end

    if character_id_text ~= nil then
        return character_id_text, NAME_PRIORITY_FALLBACK
    end

    return fallback_actor_name, NAME_PRIORITY_FALLBACK
end

local function try_get_instigator(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return nil
    end

    local ok, instigator = pcall(function()
        return actor:GetInstigator()
    end)

    instigator = unwrap_maybe_parameter(instigator)

    if ok and is_valid_object(instigator) then
        return instigator
    end

    return nil
end

local function try_get_owner(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return nil
    end

    local ok, owner = pcall(function()
        return actor:GetOwner()
    end)

    owner = unwrap_maybe_parameter(owner)

    if ok and is_valid_object(owner) then
        return owner
    end

    return nil
end

local function is_player_actor_name(actor_name)
    return actor_name ~= nil and
        actor_name:find("BP_Player_", 1, true) ~= nil
end

local function is_raid_target_name(actor_name)
    return actor_name ~= nil and
        actor_name:find("_RAID_", 1, true) ~= nil
end

local function is_pal_character(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return false
    end

    local actor_name = clean_name(object_name(actor))

    if is_player_actor_name(actor_name) then
        return false
    end

    return get_character_parameter_component(actor) ~= nil
end

local function actor_name_looks_like_raid_companion(
    actor_name
)
    local name = clean_name(actor_name)

    if name == nil or name == "" then
        return false
    end

    if is_player_actor_name(name) or
        is_raid_target_name(name) or
        name:find("BP_NPC_", 1, true) ~= nil or
        name:find("Weapon", 1, true) ~= nil or
        name:find("Bullet", 1, true) ~= nil or
        name:find("Projectile", 1, true) ~= nil then
        return false
    end

    -- Panthalus and similar Raid Army companion actors can use an _otomo
    -- class while their spawned damage instance lacks a readable Pal
    -- CharacterParameterComponent.
    return name:find("_otomo_", 1, true) ~= nil or
        name:find("_otomo_C", 1, true) ~= nil
end

local function is_probable_raid_pal_source(
    source_actor,
    source_actor_name
)
    if is_pal_character(source_actor) then
        return true
    end

    if get_known_pal_species_name(source_actor_name) ~= nil then
        return true
    end

    return actor_name_looks_like_raid_companion(
        source_actor_name
    )
end

local function apply_raid_pal_fallback(
    source_type,
    source_name,
    source_actor,
    source_actor_name,
    source_name_priority,
    defender_name
)
    if source_type ~= "OTHER" or
        not is_raid_target_name(defender_name) or
        is_raid_target_name(source_actor_name) or
        not is_probable_raid_pal_source(
            source_actor,
            source_actor_name
        ) then
        return source_type,
            source_name,
            source_name_priority
    end

    local known_name =
        get_known_pal_species_name(source_actor_name)

    if known_name ~= nil then
        source_name = known_name
        source_name_priority = NAME_PRIORITY_OFFICIAL
    else
        local resolved_name, resolved_priority =
            get_pal_name_info(
                source_actor,
                source_actor_name
            )

        source_name = resolved_name
        source_name_priority = resolved_priority
    end

    remember_raid_pal_actor(
        source_actor_name,
        source_name
    )

    return "RAID_PAL",
        source_name,
        source_name_priority
end

local function resolve_damage_source_actor(attacker)
    local current = unwrap_maybe_parameter(attacker)
    local visited = {}

    for _ = 1, 5 do
        if not is_valid_object(current) then
            break
        end

        local address_ok, address = pcall(function()
            return current:GetAddress()
        end)

        if address_ok and visited[address] then
            break
        end

        if address_ok then
            visited[address] = true
        end

        local current_name = clean_name(object_name(current))

        if is_player_actor_name(current_name) or is_players_otomo(current) then
            return current
        end

        local instigator = try_get_instigator(current)

        if instigator ~= nil and instigator ~= current then
            current = instigator
        else
            local owner = try_get_owner(current)

            if owner == nil or owner == current then
                break
            end

            current = owner
        end
    end

    return unwrap_maybe_parameter(attacker)
end

local function get_source_metadata(attacker)
    local source_actor = resolve_damage_source_actor(attacker)
    local source_actor_name = clean_name(object_name(source_actor))

    if is_player_actor_name(source_actor_name) then
        return "PLAYER",
            get_player_display_name(source_actor) or "Player",
            source_actor,
            source_actor_name,
            NAME_PRIORITY_NICKNAME
    end

    if is_players_otomo(source_actor) then
        local display_name, name_priority =
            get_pal_name_info(source_actor, source_actor_name)

        remember_known_pal_species(
            source_actor_name,
            display_name
        )

        return "PAL",
            display_name,
            source_actor,
            source_actor_name,
            name_priority
    end

    local confirmed_raid_name =
        raid_pal_name_by_actor[source_actor_name]

    if confirmed_raid_name ~= nil then
        return "RAID_PAL",
            confirmed_raid_name,
            source_actor,
            source_actor_name,
            NAME_PRIORITY_OFFICIAL
    end

    return "OTHER",
        clean_name(source_actor_name),
        source_actor,
        source_actor_name,
        NAME_PRIORITY_FALLBACK
end

local append_line
local bootstrap_partner_runtime
local emit_pal_owner
local emit_actor_name
local emit_local_player
local active_pal_actor_name = nil

-- World/server travel can destroy every world-owned UObject while the UE4SS
-- Lua VM remains alive. Never carry combat/session state into the next world.
-- This reset stores only Lua primitives/tables; no UObject is dereferenced here.
local function reset_runtime_session_state(reason)
    defender_by_component = {}
    emitted_death_by_actor = {}
    emitted_owner_by_pal_actor = {}
    owner_retry_after_by_pal_actor = {}
    emitted_name_by_actor = {}
    name_retry_after_by_actor = {}

    cached_local_player_actor_name = nil
    cached_local_player_display_name = nil
    cached_local_player_full_name = nil
    next_local_player_emit_seconds = 0.0
    active_pal_actor_name = nil

    bullet_item_by_owner_actor = {}
    active_action_by_actor = {}
    recent_weapon_by_owner_actor = {}
    emitted_weapon_identity_by_key = {}

    next_status_generation_by_key = {}
    emitted_status_conflict_by_key = {}
    active_status_by_defender = {}
    active_status_ids_by_component = {}
    last_status_snapshot_time_by_object = {}
    first_seen_status_object = {}
    last_status_by_defender = {}
    last_friendly_source_by_defender = {}
    friendly_source_by_instance_id = {}
    actor_instance_details_by_actor = {}

    partner_skill_by_internal_name = {}
    active_partner_skill_by_player_actor = {}
    partner_skills_by_player_actor = {}
    seen_partner_status_object = {}
    last_partner_status_emit_by_object = {}
    last_primary_player_hit_by_actor = {}
    last_partner_bootstrap_time_by_player = {}
    partner_buff_seen_by_player = {}
    emitted_partner_name_probe_by_key = {}
    raid_pal_name_by_actor = {}
    friendly_damaged_defender_by_actor = {}

    -- These are class-default/static utility objects, but reacquiring them
    -- after travel avoids carrying even engine utility wrappers across worlds.
    pal_ui_utility = nil
    pal_utility = nil

    print(string.format(
        "[RogueModeTelemetry] Runtime session reset: %s\n",
        tostring(reason or "unknown")
    ))
end

local function emit_pal_state(actor, is_active, source)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) or not is_players_otomo(actor) then
        return
    end

    local actor_name = clean_name(object_name(actor))
    local display_name, name_priority =
        get_pal_name_info(actor, actor_name)
    local state = is_active and "ACTIVE" or "INACTIVE"

    if emit_actor_name ~= nil then
        emit_actor_name(
            actor,
            display_name,
            name_priority,
            "Pal state"
        )
    end

    if emit_local_player ~= nil then
        emit_local_player(
            actor,
            "Pal state",
            true
        )
    end

    if emit_pal_owner ~= nil then
        emit_pal_owner(actor, "Pal state")
    end

    if is_active then
        if active_pal_actor_name == actor_name then
            return
        end

        active_pal_actor_name = actor_name
    else
        if active_pal_actor_name ~= actor_name then
            return
        end

        active_pal_actor_name = nil
    end

    append_line(string.format(
        "P|%.3f|%s|%s|%s\n",
        os.clock(),
        state,
        sanitize_field(actor_name),
        sanitize_field(display_name)
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][PAL %s] %s source=%s\n",
        state,
        display_name,
        source
    ))
end

local telemetry_state = {
    file_handle = nil,
    pending_line_count = 0,
    last_flush_seconds = 0.0,
    startup_reset_pending = true,
    last_combat_seconds = 0.0,
    max_bytes = 25 * 1024 * 1024,
    idle_seconds = 30.0
}

local function ensure_telemetry_file()
    if telemetry_state.file_handle ~= nil then
        return true
    end

    local open_mode = telemetry_state.startup_reset_pending
        and "w"
        or "a"

    telemetry_state.file_handle = io.open(OUTPUT_FILE, open_mode)

    if telemetry_state.file_handle == nil then
        print(
            "[RogueModeTelemetry][ERROR] "
            .. "Could not open telemetry file: "
            .. OUTPUT_FILE
            .. "\n"
        )
        return false
    end

    if telemetry_state.startup_reset_pending then
        telemetry_state.startup_reset_pending = false
        telemetry_state.pending_line_count = 0
        telemetry_state.last_flush_seconds = os.clock()

        print(string.format(
            "[RogueModeTelemetry] Startup reset completed: %s\n",
            OUTPUT_FILE
        ))
    end

    return true
end

local function flush_telemetry_file()
    if telemetry_state.file_handle == nil then
        return false
    end

    local ok = pcall(function()
        telemetry_state.file_handle:flush()
    end)

    if not ok then
        pcall(function()
            telemetry_state.file_handle:close()
        end)
        telemetry_state.file_handle = nil
        telemetry_state.pending_line_count = 0
        return false
    end

    telemetry_state.pending_line_count = 0
    telemetry_state.last_flush_seconds = os.clock()
    return true
end

append_line = function(line)
    local now = os.clock()
    local record_type = string.sub(line, 1, 2)
    local combat_record =
        record_type == "D|" or
        record_type == "M|" or
        record_type == "T|"

    -- Keep telemetry bounded without truncating an active encounter. After
    -- the file exceeds 25 MB, the next write following at least 30 seconds
    -- without damage safely starts a fresh telemetry segment.
    if not telemetry_state.startup_reset_pending and
        telemetry_state.file_handle ~= nil and
        now - telemetry_state.last_combat_seconds >=
            telemetry_state.idle_seconds then
        local size_ok, file_size = pcall(function()
            return telemetry_state.file_handle:seek("end")
        end)

        if size_ok and
            tonumber(file_size) ~= nil and
            tonumber(file_size) >= telemetry_state.max_bytes then
            pcall(function()
                telemetry_state.file_handle:flush()
                telemetry_state.file_handle:close()
            end)

            telemetry_state.file_handle = io.open(OUTPUT_FILE, "w")
            telemetry_state.pending_line_count = 0
            telemetry_state.last_flush_seconds = now

            if telemetry_state.file_handle ~= nil then
                telemetry_state.file_handle:write(string.format(
                    "V|%.3f|v0.9.0-RC7|SIZE_LIMIT_RESET\n",
                    now
                ))
                telemetry_state.file_handle:flush()

                print(string.format(
                    "[RogueModeTelemetry] Size-limit reset completed: %s\n",
                    OUTPUT_FILE
                ))
            else
                print(
                    "[RogueModeTelemetry][ERROR] "
                    .. "Could not reset oversized telemetry file: "
                    .. OUTPUT_FILE
                    .. "\n"
                )
            end
        end
    end

    if combat_record then
        telemetry_state.last_combat_seconds = now
    end

    if not ensure_telemetry_file() then
        return false
    end

    local ok = pcall(function()
        telemetry_state.file_handle:write(line)
    end)

    if not ok then
        pcall(function()
            telemetry_state.file_handle:close()
        end)
        telemetry_state.file_handle = nil

        if not ensure_telemetry_file() then
            return false
        end

        local retry_ok = pcall(function()
            telemetry_state.file_handle:write(line)
        end)

        if not retry_ok then
            telemetry_state.file_handle = nil
            return false
        end
    end

    telemetry_state.pending_line_count =
        telemetry_state.pending_line_count + 1

    local important_record =
        record_type == "M|" or
        record_type == "T|" or
        record_type == "X|" or
        record_type == "P|" or
        record_type == "O|" or
        record_type == "L|" or
        record_type == "V|"

    if important_record or
        telemetry_state.pending_line_count >= 32 or
        now - telemetry_state.last_flush_seconds >= 0.050 then
        return flush_telemetry_file()
    end

    return true
end

local function append_diagnostic_line(line)
    if not ENABLE_VERBOSE_DIAGNOSTIC_RECORDS then
        return true
    end

    return append_line(line)
end

local function get_pal_utility()
    if is_valid_object(pal_utility) then
        return pal_utility
    end

    local ok, utility = pcall(function()
        return StaticFindObject(
            "/Script/Pal.Default__PalUtility"
        )
    end)

    utility = unwrap_maybe_parameter(utility)

    if ok and is_valid_object(utility) then
        pal_utility = utility
        return utility
    end

    return nil
end

local function resolve_local_player(world_context)
    local utility = get_pal_utility()

    if utility == nil then
        return nil
    end

    world_context = unwrap_maybe_parameter(world_context)

    if not is_valid_object(world_context) then
        return nil
    end

    -- Intentionally reacquire the local player every time. A player UObject
    -- cached across server travel can become a stale native pointer, and Lua
    -- pcall cannot catch an EXCEPTION_ACCESS_VIOLATION from that pointer.
    local ok, player = pcall(function()
        return utility:GetPlayerCharacter(world_context)
    end)

    player = unwrap_maybe_parameter(player)

    if not ok or not is_valid_object(player) then
        return nil
    end

    local player_full_name = object_name(player)

    if cached_local_player_full_name ~= nil and
        player_full_name ~= "unknown" and
        player_full_name ~= "invalid" and
        player_full_name ~= "nil" and
        player_full_name ~= cached_local_player_full_name then
        reset_runtime_session_state("LOCAL_PLAYER_CHANGED")
    end

    cached_local_player_full_name = player_full_name
    return player
end

emit_local_player = function(
    world_context,
    source,
    force_emit
)
    local player = resolve_local_player(world_context)

    if not is_valid_object(player) then
        return nil
    end

    local actor_name = clean_name(object_name(player))

    if not is_player_actor_name(actor_name) then
        return nil
    end

    local display_name =
        get_player_display_name(player) or
        clean_name(actor_name)

    local now = os.clock()
    local actor_changed =
        cached_local_player_actor_name ~= actor_name
    local name_changed =
        cached_local_player_display_name ~= display_name

    cached_local_player_actor_name = actor_name
    cached_local_player_display_name = display_name

    if not force_emit and
        not actor_changed and
        not name_changed and
        now < next_local_player_emit_seconds then
        return player
    end

    local written = append_line(string.format(
        "L|%.3f|%s|%s\n",
        now,
        sanitize_field(actor_name),
        sanitize_field(display_name)
    ))

    if not written then
        return player
    end

    -- Re-emit periodically so a tracker started after Palworld still learns
    -- the local player during the next active-Pal event or combat exchange.
    next_local_player_emit_seconds = now + 2.0

    verbose_print(string.format(
        "[RogueModeTelemetry][LOCAL PLAYER] %s -> %s source=%s\n",
        actor_name,
        display_name,
        source
    ))

    return player
end

emit_actor_name = function(
    actor,
    display_name,
    priority,
    source
)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return
    end

    local actor_name = clean_name(object_name(actor))
    local resolved_name = meaningful_name(display_name)

    if resolved_name == nil then
        return
    end

    priority = tonumber(priority) or NAME_PRIORITY_FALLBACK
    priority = math.max(
        NAME_PRIORITY_FALLBACK,
        math.min(NAME_PRIORITY_NICKNAME, priority)
    )

    local previous = emitted_name_by_actor[actor_name]
    local now = os.clock()

    if previous ~= nil then
        if previous.priority > priority then
            return
        end

        if previous.priority == priority and
            previous.name == resolved_name then
            if priority > NAME_PRIORITY_FALLBACK then
                return
            end

            local retry_after = name_retry_after_by_actor[actor_name]

            if retry_after ~= nil and now < retry_after then
                return
            end
        end
    end

    local written = append_line(string.format(
        "N|%.3f|%s|%s|%d\n",
        now,
        sanitize_field(actor_name),
        sanitize_field(resolved_name),
        priority
    ))

    if not written then
        return
    end

    emitted_name_by_actor[actor_name] = {
        name = resolved_name,
        priority = priority
    }

    if priority == NAME_PRIORITY_FALLBACK then
        name_retry_after_by_actor[actor_name] = now + 2.0
    else
        name_retry_after_by_actor[actor_name] = nil
    end

    verbose_print(string.format(
        "[RogueModeTelemetry][NAME] %s -> %s priority=%d source=%s\n",
        actor_name,
        resolved_name,
        priority,
        source
    ))
end

emit_pal_owner = function(actor, source)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return
    end

    local actor_name = clean_name(object_name(actor))

    if emitted_owner_by_pal_actor[actor_name] then
        return
    end

    local now = os.clock()
    local retry_after = owner_retry_after_by_pal_actor[actor_name]

    if retry_after ~= nil and now < retry_after then
        return
    end

    -- If replication is not ready on the first hit, retry at most once every
    -- two seconds rather than reflecting the Trainer property on every hit.
    owner_retry_after_by_pal_actor[actor_name] = now + 2.0

    local component = get_character_parameter_component(actor)

    if component == nil then
        return
    end

    local trainer_ok, trainer = pcall(function()
        return component.Trainer
    end)

    trainer = unwrap_maybe_parameter(trainer)

    if not trainer_ok or not is_valid_object(trainer) then
        return
    end

    local owner_actor_name = clean_name(object_name(trainer))

    if not is_player_actor_name(owner_actor_name) then
        return
    end

    local owner_display_name =
        get_player_display_name(trainer) or
        clean_name(owner_actor_name)

    local written = append_line(string.format(
        "O|%.3f|%s|%s|%s\n",
        now,
        sanitize_field(actor_name),
        sanitize_field(owner_actor_name),
        sanitize_field(owner_display_name)
    ))

    if not written then
        return
    end

    emitted_owner_by_pal_actor[actor_name] = true
    owner_retry_after_by_pal_actor[actor_name] = nil

    if bootstrap_partner_runtime ~= nil then
        bootstrap_partner_runtime(
            owner_actor_name,
            now,
            false
        )
    end

    verbose_print(string.format(
        "[RogueModeTelemetry][OWNER] %s -> %s source=%s\n",
        actor_name,
        owner_display_name,
        source
    ))
end

local function get_component_key(context_parameter)
    local component = get_value(context_parameter)
    local full_name = object_name(component)

    if full_name == "nil" or full_name == "invalid" or full_name == "unknown" then
        return nil, full_name
    end

    return full_name, full_name
end

local function get_component_owner_name(context_parameter)
    local component = get_value(context_parameter)

    if component == nil then
        return "unknown"
    end

    local ok, owner = pcall(function()
        if component:IsValid() then
            return component:GetOwner()
        end

        return nil
    end)

    if not ok then
        return "unknown"
    end

    return clean_name(object_name(owner))
end

local function get_dead_info_self_actor(dead_info_parameter)
    local dead_info = get_value(dead_info_parameter)

    if dead_info == nil then
        return nil
    end

    local ok, self_actor = pcall(function()
        return dead_info.SelfActor
    end)

    if not ok then
        return nil
    end

    return unwrap_maybe_parameter(self_actor)
end

local function emit_death(actor_name, source)
    actor_name = clean_name(actor_name)

    if actor_name == "nil" or actor_name == "invalid" or actor_name == "unknown" then
        verbose_print(string.format(
            "[RogueModeTelemetry][DEATH DEBUG] unresolved source=%s actor=%s\n",
            source,
            actor_name
        ))
        return
    end

    if emitted_death_by_actor[actor_name] then
        return
    end

    emitted_death_by_actor[actor_name] = true
    friendly_damaged_defender_by_actor[actor_name] = nil

    append_line(string.format(
        "X|%.3f|%s\n",
        os.clock(),
        actor_name
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][DEATH] %s source=%s\n",
        actor_name,
        source
    ))
end

local function clear_actor_runtime_state(actor_name)
    actor_name = clean_name(actor_name)

    friendly_damaged_defender_by_actor[actor_name] = nil
    active_action_by_actor[actor_name] = nil
    recent_weapon_by_owner_actor[actor_name] = nil
    bullet_item_by_owner_actor[actor_name] = nil
    last_primary_player_hit_by_actor[actor_name] = nil
    active_status_by_defender[actor_name] = nil
    last_status_by_defender[actor_name] = nil
    last_friendly_source_by_defender[actor_name] = nil
    actor_instance_details_by_actor[actor_name] = nil
    emitted_owner_by_pal_actor[actor_name] = nil
    owner_retry_after_by_pal_actor[actor_name] = nil
    emitted_name_by_actor[actor_name] = nil
    name_retry_after_by_actor[actor_name] = nil
    raid_pal_name_by_actor[actor_name] = nil
end

local function emit_tracked_actor_removal(context_parameter, source)
    local actor = get_value(context_parameter)
    local actor_name = clean_name(object_name(actor))
    local local_player_leaving =
        cached_local_player_actor_name ~= nil and
        actor_name == cached_local_player_actor_name

    if friendly_damaged_defender_by_actor[actor_name] then
        emit_death(actor_name, source)
    end

    if local_player_leaving then
        reset_runtime_session_state(
            "LOCAL_PLAYER_REMOVAL:" .. tostring(source)
        )
        return
    end

    clear_actor_runtime_state(actor_name)
end

local function read_struct_field(struct_value, field_name)
    struct_value = unwrap_maybe_parameter(struct_value)

    if struct_value == nil then
        return nil
    end

    local ok, value = pcall(function()
        return struct_value[field_name]
    end)

    if not ok then
        return nil
    end

    return unwrap_maybe_parameter(value)
end

local function diagnostic_value_text(value)
    value = unwrap_maybe_parameter(value)

    if value == nil then
        return "unknown"
    end

    local value_type = type(value)

    if value_type == "string" or
        value_type == "number" or
        value_type == "boolean" then
        return sanitize_field(tostring(value))
    end

    local to_string_ok, to_string_value = pcall(function()
        return value:ToString()
    end)

    if to_string_ok and to_string_value ~= nil then
        return sanitize_field(to_string_value)
    end

    local get_name_ok, get_name_value = pcall(function()
        return value:GetName()
    end)

    if get_name_ok and get_name_value ~= nil then
        return sanitize_field(get_name_value)
    end

    return sanitize_field(tostring(value))
end

local function diagnostic_bool_text(value)
    value = unwrap_maybe_parameter(value)

    if value == true then
        return "true"
    end

    if value == false then
        return "false"
    end

    local numeric = tonumber(value)

    if numeric ~= nil then
        return numeric ~= 0 and "true" or "false"
    end

    return diagnostic_value_text(value)
end

local function get_waza_numeric_id(waza_value)
    waza_value = unwrap_maybe_parameter(waza_value)

    local numeric = tonumber(waza_value)

    if numeric ~= nil then
        return numeric
    end

    local ok, value = pcall(function()
        return waza_value:GetValue()
    end)

    if ok then
        return tonumber(value)
    end

    return nil
end

local function resolve_waza_name(world_context, waza_value)
    local numeric_id = get_waza_numeric_id(waza_value)
    local cache_key = numeric_id ~= nil
        and tostring(numeric_id)
        or diagnostic_value_text(waza_value)

    local cached = resolved_waza_name_by_id[cache_key]

    if cached ~= nil then
        return cached
    end

    local utility = get_pal_ui_utility()

    if utility == nil then
        return nil
    end

    world_context = unwrap_maybe_parameter(world_context)

    if not is_valid_object(world_context) then
        return nil
    end

    local out_parameters = {}

    local call_ok, return_value = pcall(function()
        return utility:GetWazaName(
            world_context,
            unwrap_maybe_parameter(waza_value),
            out_parameters
        )
    end)

    if not call_ok then
        return nil
    end

    local resolved_name = meaningful_name(
        out_parameters.outName or
        out_parameters.OutName or
        out_parameters.WazaName or
        out_parameters[1]
    )

    if resolved_name == nil then
        resolved_name = meaningful_name(return_value)
    end

    if resolved_name ~= nil then
        resolved_waza_name_by_id[cache_key] = resolved_name
    end

    return resolved_name
end

local function get_object_class_name(object)
    object = unwrap_maybe_parameter(object)

    if not is_valid_object(object) then
        return "unknown"
    end

    local ok, result = pcall(function()
        local class_object = object:GetClass()

        if class_object == nil then
            return nil
        end

        local name_ok, name = pcall(function()
            return class_object:GetName()
        end)

        if name_ok and name ~= nil then
            return name
        end

        return class_object:GetFullName()
    end)

    if ok and result ~= nil then
        return diagnostic_value_text(result)
    end

    return "unknown"
end

local function call_object_method(object, method_name)
    object = unwrap_maybe_parameter(object)

    if not is_valid_object(object) then
        return nil
    end

    local ok, result = pcall(function()
        return object[method_name](object)
    end)

    if ok then
        return unwrap_maybe_parameter(result)
    end

    return nil
end

local function get_action_waza_value(action)
    local value = call_object_method(action, "GetWazaID")

    if value ~= nil then
        return value
    end

    return read_struct_field(action, "WazaID")
end

local function emit_action_record(
    phase,
    action,
    character_override,
    allow_player
)
    action = unwrap_maybe_parameter(action)

    if not is_valid_object(action) then
        return
    end

    local character = unwrap_maybe_parameter(character_override)

    if not is_valid_object(character) then
        character = call_object_method(action, "GetActionCharacter")
    end

    if not is_valid_object(character) then
        return
    end

    local source_type,
        source_name,
        source_actor,
        source_actor_name,
        source_name_priority =
        get_source_metadata(character)

    local target = call_object_method(action, "GetActionTarget")
    local target_name = clean_name(object_name(target))

    source_type,
        source_name,
        source_name_priority =
        apply_raid_pal_fallback(
            source_type,
            source_name,
            source_actor,
            source_actor_name,
            source_name_priority,
            target_name
        )

    local allowed =
        source_type == "PAL" or
        source_type == "RAID_PAL" or
        (allow_player and source_type == "PLAYER")

    if not allowed then
        return
    end
    local simple_name = diagnostic_value_text(
        call_object_method(action, "GetSimpleName")
    )
    local action_id = diagnostic_value_text(
        call_object_method(action, "GetActionID")
    )
    local waza_value = get_action_waza_value(action)
    local waza_id = get_waza_numeric_id(waza_value)
    local waza_raw = diagnostic_value_text(waza_value)
    local waza_name = "unresolved"

    if waza_value ~= nil then
        waza_name =
            resolve_waza_name(character, waza_value) or
            "unresolved"
    end

    local action_object_name = clean_name(object_name(action))
    local action_class_name = get_object_class_name(action)

    local now = os.clock()

    if source_type == "PAL" or source_type == "RAID_PAL" then
        local action_key =
            waza_id ~= nil and tostring(waza_id) or waza_raw

        if phase == "BEGIN" and
            waza_name ~= "ACTION_SKILL_None" and
            waza_name ~= "unresolved" then
            active_action_by_actor[source_actor_name] = {
                WazaId = action_key,
                WazaName = waza_name,
                ActionObject = action_object_name,
                Target = target_name,
                StartTime = now,
                EndTime = nil,
                Phase = "ACTIVE"
            }
        elseif phase == "END" or phase == "BREAK" then
            local active = active_action_by_actor[source_actor_name]

            if active ~= nil and
                (active.ActionObject == action_object_name or
                 active.WazaId == action_key) then
                active.EndTime = now
                active.Phase = phase
            end
        end
    end

    action_lifecycle_sequence = action_lifecycle_sequence + 1

    append_line(string.format(
        "Q|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        now,
        action_lifecycle_sequence,
        sanitize_field(phase),
        sanitize_field(source_actor_name),
        sanitize_field(source_type),
        sanitize_field(source_name),
        sanitize_field(target_name),
        sanitize_field(action_object_name),
        sanitize_field(action_class_name),
        sanitize_field(simple_name),
        sanitize_field(action_id),
        sanitize_field(
            waza_id ~= nil and tostring(waza_id) or waza_raw
        ),
        sanitize_field(waza_name)
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][ACTION %s] %s action=%s "
        .. "waza=%s name=%s target=%s\n",
        phase,
        source_name,
        simple_name,
        waza_raw,
        waza_name,
        target_name
    ))
end

local function emit_action_lifecycle(phase, context_parameter)
    local action = unwrap_maybe_parameter(context_parameter)

    emit_action_record(
        phase,
        action,
        nil,
        false
    )
end

local function get_shooter_owner(component)
    return call_object_method(component, "GetOwner")
end

local function get_shooter_weapon(component, explicit_weapon)
    local weapon = unwrap_maybe_parameter(explicit_weapon)

    if is_valid_object(weapon) then
        return weapon
    end

    weapon = call_object_method(component, "GetHasWeapon")

    if is_valid_object(weapon) then
        return weapon
    end

    weapon = read_struct_field(component, "HasWeapon")

    if is_valid_object(weapon) then
        return weapon
    end

    return nil
end

local function get_weapon_item_struct(weapon)
    local item_id = call_object_method(weapon, "GetItemId")

    if item_id ~= nil then
        return item_id
    end

    return read_struct_field(weapon, "ownItemID")
end

local function get_weapon_static_item_id(weapon)
    local item_struct = get_weapon_item_struct(weapon)

    if item_struct == nil then
        return nil, "unknown"
    end

    local static_id = read_struct_field(
        item_struct,
        "StaticId"
    )

    if static_id == nil then
        static_id = read_struct_field(
            item_struct,
            "StaticID"
        )
    end

    if static_id == nil then
        return nil, "unknown"
    end

    return static_id, diagnostic_value_text(static_id)
end

local function resolve_item_name(
    world_context,
    static_item_id
)
    static_item_id = unwrap_maybe_parameter(static_item_id)

    if static_item_id == nil then
        return nil
    end

    local cache_key = diagnostic_value_text(static_item_id)
    local cached = resolved_item_name_by_static_id[cache_key]

    if cached ~= nil then
        return cached
    end

    local utility = get_pal_ui_utility()

    if utility == nil then
        return nil
    end

    world_context = unwrap_maybe_parameter(world_context)

    if not is_valid_object(world_context) then
        return nil
    end

    local out_parameters = {}

    local ok, return_value = pcall(function()
        return utility:GetItemName(
            world_context,
            static_item_id,
            out_parameters
        )
    end)

    if not ok then
        return nil
    end

    local resolved_name = meaningful_name(
        out_parameters.outName or
        out_parameters.OutName or
        out_parameters.ItemName or
        out_parameters[1]
    )

    if resolved_name == nil then
        resolved_name = meaningful_name(return_value)
    end

    if resolved_name ~= nil then
        resolved_item_name_by_static_id[cache_key] =
            resolved_name
    end

    return resolved_name
end

local function emit_weapon_identity(
    owner_actor,
    source_type,
    source_name,
    weapon,
    static_id_text,
    item_name,
    weapon_type,
    bullet_id,
    override_weapon_type
)
    local weapon_name = clean_name(object_name(weapon))
    local weapon_class = get_object_class_name(weapon)
    local identity_key = table.concat({
        owner_actor,
        weapon_name,
        static_id_text,
        item_name,
        weapon_type,
        bullet_id
    }, "|")

    if emitted_weapon_identity_by_key[identity_key] then
        return
    end

    emitted_weapon_identity_by_key[identity_key] = true
    weapon_identity_sequence = weapon_identity_sequence + 1

    append_diagnostic_line(string.format(
        "I|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        os.clock(),
        weapon_identity_sequence,
        sanitize_field(owner_actor),
        sanitize_field(source_type),
        sanitize_field(source_name),
        sanitize_field(weapon_name),
        sanitize_field(weapon_class),
        sanitize_field(static_id_text),
        sanitize_field(item_name),
        sanitize_field(weapon_type),
        sanitize_field(bullet_id),
        sanitize_field(override_weapon_type)
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][WEAPON IDENTITY] %s "
        .. "weapon=%s static=%s display=%s type=%s bullet=%s\n",
        source_name,
        weapon_name,
        static_id_text,
        item_name,
        weapon_type,
        bullet_id
    ))
end

local function get_weapon_bullet_id(weapon)
    local bullet_id = call_object_method(
        weapon,
        "GetCurrentBulletItemId"
    )

    if bullet_id ~= nil then
        return diagnostic_value_text(bullet_id)
    end

    bullet_id = read_struct_field(weapon, "BulletItemName")

    if bullet_id ~= nil then
        return diagnostic_value_text(bullet_id)
    end

    return "unknown"
end

local function emit_weapon_probe(
    event_name,
    context_parameter,
    weapon_parameter,
    detail_parameter
)
    local component = unwrap_maybe_parameter(context_parameter)

    if not is_valid_object(component) then
        return
    end

    local owner = get_shooter_owner(component)

    if not is_valid_object(owner) then
        return
    end

    local source_type,
        source_name,
        source_actor,
        source_actor_name =
        get_source_metadata(owner)

    if source_type ~= "PLAYER" and
        source_type ~= "PAL" and
        source_type ~= "RAID_PAL" then
        return
    end

    local owner_key = source_actor_name
    local weapon = get_shooter_weapon(
        component,
        weapon_parameter
    )

    -- Never retain a live weapon UObject between callbacks. Weapon actors are
    -- world-owned and become unsafe immediately when changing servers/maps.
    -- Primitive weapon metadata is already cached in recent_weapon_by_owner_actor.
    local explicit_detail = diagnostic_value_text(detail_parameter)

    if not is_valid_object(weapon) then
        if event_name == "CHANGED_BULLET" and
            explicit_detail ~= "unknown" then
            bullet_item_by_owner_actor[owner_key] = explicit_detail
        end

        return
    end

    if event_name == "CHANGED_BULLET" and
        explicit_detail ~= "unknown" then
        bullet_item_by_owner_actor[owner_key] = explicit_detail
    end

    local weapon_name = clean_name(object_name(weapon))
    local weapon_class = get_object_class_name(weapon)
    local static_item_id, static_item_id_text =
        get_weapon_static_item_id(weapon)
    local item_name =
        resolve_item_name(owner, static_item_id) or
        "unresolved"
    local weapon_type = diagnostic_value_text(
        read_struct_field(weapon, "WeaponType")
    )
    local bullet_id = get_weapon_bullet_id(weapon)

    if bullet_id == "unknown" and
        bullet_item_by_owner_actor[owner_key] ~= nil then
        bullet_id = bullet_item_by_owner_actor[owner_key]
    end

    local override_weapon_type = diagnostic_value_text(
        read_struct_field(component, "OverrideWeaponType")
    )
    local component_name = clean_name(object_name(component))

    local now = os.clock()

    recent_weapon_by_owner_actor[owner_key] = {
        WeaponActor = weapon_name,
        WeaponClass = weapon_class,
        StaticItemId = static_item_id_text,
        ItemName = item_name,
        WeaponType = weapon_type,
        BulletId = bullet_id,
        OverrideWeaponType = override_weapon_type,
        Event = event_name,
        EventTime = now
    }

    emit_weapon_identity(
        owner_key,
        source_type,
        source_name,
        weapon,
        static_item_id_text,
        item_name,
        weapon_type,
        bullet_id,
        override_weapon_type
    )

    weapon_probe_sequence = weapon_probe_sequence + 1

    append_diagnostic_line(string.format(
        "W|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        now,
        weapon_probe_sequence,
        sanitize_field(event_name),
        sanitize_field(source_actor_name),
        sanitize_field(source_type),
        sanitize_field(source_name),
        sanitize_field(weapon_name),
        sanitize_field(weapon_class),
        sanitize_field(static_item_id_text),
        sanitize_field(weapon_type),
        sanitize_field(bullet_id),
        sanitize_field(override_weapon_type),
        sanitize_field(explicit_detail),
        sanitize_field(component_name)
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][WEAPON %s] %s weapon=%s "
        .. "item=%s type=%s bullet=%s override=%s\n",
        event_name,
        source_name,
        weapon_name,
        item_name,
        weapon_type,
        bullet_id,
        override_weapon_type
    ))
end


local STATUS_NAMES_BY_ID = {
    [5] = "Poison",
    [14] = "Drown",
    [17] = "Fall Damage",
    [18] = "Lava Damage",
    [19] = "Burn",
    [20] = "Wetness",
    [21] = "Freeze",
    [22] = "Electrical",
    [23] = "Muddy",
    [24] = "Ivy Cling",
    [25] = "Darkness",
    [61] = "Player Arrow Explosion"
}

local STATUS_IDS_BY_NAME = {
    poison = 5,
    drown = 14,
    falldamage = 17,
    lavadamage = 18,
    burn = 19,
    wetness = 20,
    freeze = 21,
    electrical = 22,
    muddy = 23,
    ivycling = 24,
    darkness = 25,
    playerarrowexplosion = 61
}

local TRACKED_STATUS_ID_LIST = {
    5,   -- Poison
    19,  -- Burn
    61   -- Player Arrow Explosion
}

local TRACKED_STATUS_ID_SET = {
    [5] = true,
    [19] = true
}

local function normalize_identity_key(value)
    local text = diagnostic_value_text(value)

    if text == "unknown" or
        text == "nil" or
        text == "invalid" or
        text:find("table:", 1, true) ~= nil or
        text:find("TrivialObject:", 1, true) ~= nil or
        text:find("UScriptStruct:", 1, true) ~= nil then
        return nil
    end

    text = text:lower():gsub("[^%w]", "")

    if text == "" or text == "0" then
        return nil
    end

    return text
end

local function format_guid_value(value)
    value = unwrap_maybe_parameter(value)

    if value == nil then
        return "unknown"
    end

    local direct = diagnostic_value_text(value)

    if direct ~= "unknown" and
        direct:find("table:", 1, true) == nil and
        direct:find("TrivialObject:", 1, true) == nil and
        direct:find("UScriptStruct:", 1, true) == nil then
        return direct
    end

    local a = tonumber(read_struct_field(value, "A"))
    local b = tonumber(read_struct_field(value, "B"))
    local c = tonumber(read_struct_field(value, "C"))
    local d = tonumber(read_struct_field(value, "D"))

    if a == nil or b == nil or c == nil or d == nil then
        return direct
    end

    local ok, formatted = pcall(function()
        return string.format(
            "%08X-%08X-%08X-%08X",
            a,
            b,
            c,
            d
        )
    end)

    if ok then
        return formatted
    end

    return table.concat({
        tostring(a),
        tostring(b),
        tostring(c),
        tostring(d)
    }, "-")
end

local function get_component_owner_object(context_parameter)
    local component = get_value(context_parameter)

    if component == nil then
        component = unwrap_maybe_parameter(context_parameter)
    end

    if not is_valid_object(component) then
        return nil
    end

    local ok, owner = pcall(function()
        return component:GetOwner()
    end)

    owner = unwrap_maybe_parameter(owner)

    if ok and is_valid_object(owner) then
        return owner
    end

    return nil
end

local function get_pal_instance_details(instance_value)
    instance_value = unwrap_maybe_parameter(instance_value)

    if instance_value == nil then
        return {
            Full = "unknown",
            Instance = "unknown",
            Player = "unknown",
            DebugName = "unknown"
        }
    end

    local instance_guid =
        read_struct_field(instance_value, "InstanceId")
    local player_guid =
        read_struct_field(instance_value, "PlayerUId")
    local debug_name =
        diagnostic_value_text(
            read_struct_field(instance_value, "DebugName")
        )

    return {
        Full = diagnostic_value_text(instance_value),
        Instance = format_guid_value(instance_guid),
        Player = format_guid_value(player_guid),
        DebugName = debug_name
    }
end

local function get_actor_instance_details(actor)
    actor = unwrap_maybe_parameter(actor)

    if not is_valid_object(actor) then
        return {
            Full = "unknown",
            Instance = "unknown",
            Player = "unknown",
            DebugName = "unknown"
        }
    end

    local actor_name = clean_name(object_name(actor))
    local cached =
        actor_instance_details_by_actor[actor_name]

    if cached ~= nil then
        return cached
    end

    local utility = get_pal_utility()

    if utility == nil then
        return {
            Full = "unknown",
            Instance = "unknown",
            Player = "unknown",
            DebugName = "unknown"
        }
    end

    local ok, parameter = pcall(function()
        return utility:GetIndividualCharacterParameterByActor(actor)
    end)

    parameter = unwrap_maybe_parameter(parameter)

    if not ok or not is_valid_object(parameter) then
        return {
            Full = "unknown",
            Instance = "unknown",
            Player = "unknown",
            DebugName = "unknown"
        }
    end

    local details = get_pal_instance_details(
        read_struct_field(parameter, "IndividualId")
    )

    if normalize_identity_key(details.Instance) ~= nil then
        actor_instance_details_by_actor[actor_name] = details
    end

    return details
end

local function get_status_info(status_parameter)
    local raw_value = unwrap_maybe_parameter(status_parameter)
    local raw_text = diagnostic_value_text(raw_value)
    local numeric_id = nil

    if type(raw_value) == "number" then
        numeric_id = raw_value
    elseif type(raw_value) == "string" then
        numeric_id = tonumber(raw_value)
    end

    if numeric_id == nil then
        numeric_id = tonumber(raw_text)
    end

    if numeric_id == nil and raw_value ~= nil then
        local method_names = {
            "GetValue",
            "GetInt",
            "GetValueAsInt"
        }

        for _, method_name in ipairs(method_names) do
            local ok, extracted = pcall(function()
                return raw_value[method_name](raw_value)
            end)

            extracted = unwrap_maybe_parameter(extracted)

            if ok then
                numeric_id =
                    tonumber(extracted) or
                    tonumber(diagnostic_value_text(extracted))
            end

            if numeric_id ~= nil then
                break
            end
        end
    end

    local enum_name =
        raw_text:match("::([%w_]+)$") or
        raw_text:match("EPalStatusID_([%w_]+)$") or
        raw_text:match("EPalStatusID::([%w_]+)")

    if numeric_id == nil and enum_name ~= nil then
        numeric_id = STATUS_IDS_BY_NAME[
            enum_name:lower():gsub("[^%w]", "")
        ]
    end

    local status_name =
        numeric_id ~= nil and STATUS_NAMES_BY_ID[numeric_id] or nil

    if status_name == nil and enum_name ~= nil then
        status_name = enum_name:gsub("_", " ")
    end

    if status_name == nil then
        status_name = raw_text
    end

    local status_id_text =
        numeric_id ~= nil and tostring(numeric_id) or raw_text

    local is_tracked =
        numeric_id ~= nil and
        TRACKED_STATUS_ID_SET[numeric_id] == true

    return status_id_text, status_name, numeric_id, is_tracked
end

local function get_status_parameter_details(param_parameter)
    local param = unwrap_maybe_parameter(param_parameter)

    if param == nil then
        return {
            Index = "unknown",
            Name = "unknown",
            FloatValue = "unknown",
            Instance = "unknown",
            InstancePlayer = "unknown",
            InstanceDebugName = "unknown"
        }
    end

    local instance_details = get_pal_instance_details(
        read_struct_field(param, "GeneralInstanceID")
    )

    return {
        Index = diagnostic_value_text(
            read_struct_field(param, "GeneralIndex")
        ),
        Name = diagnostic_value_text(
            read_struct_field(param, "GeneralName")
        ),
        FloatValue = diagnostic_value_text(
            read_struct_field(param, "GeneralFloatValue")
        ),
        Instance = instance_details.Instance,
        InstancePlayer = instance_details.Player,
        InstanceDebugName = instance_details.DebugName
    }
end

local function copy_source_snapshot(source)
    if source == nil then
        return nil
    end

    return {
        Type = source.Type,
        Actor = source.Actor,
        Name = source.Name,
        Label = source.Label,
        Identity = source.Identity,
        State = source.State,
        Time = source.Time,
        ActorInstance = source.ActorInstance,
        ActorPlayer = source.ActorPlayer
    }
end

local function get_source_snapshot_by_identity(identity_text)
    local identity_key = normalize_identity_key(identity_text)

    if identity_key == nil then
        return nil
    end

    return friendly_source_by_instance_id[identity_key]
end

local function resolve_status_source(
    defender_name,
    param_details,
    invoker_text,
    now
)
    local source =
        get_source_snapshot_by_identity(param_details.Instance)

    if source ~= nil then
        return copy_source_snapshot(source), "PARAM_INSTANCE"
    end

    source = get_source_snapshot_by_identity(invoker_text)

    if source ~= nil then
        return copy_source_snapshot(source), "INVOKER_INSTANCE"
    end

    source = last_friendly_source_by_defender[defender_name]

    if source ~= nil then
        local age = now - (source.Time or now)

        if age <= 2.5 then
            return copy_source_snapshot(source), "RECENT_HIT"
        end
    end

    return nil, "UNRESOLVED"
end

local function source_field(source, field_name)
    if source == nil then
        return "unknown"
    end

    return source[field_name] or "unknown"
end

local function source_signature(source)
    if source == nil then
        return "unknown"
    end

    return table.concat({
        tostring(source.Type or "unknown"),
        tostring(source.Actor or "unknown"),
        tostring(source.Label or "unknown"),
        tostring(source.Identity or "unknown")
    }, "|")
end

local function should_emit_status_conflict(
    defender_name,
    status_id,
    generation,
    candidate_source,
    reason
)
    local key = table.concat({
        defender_name,
        tostring(status_id),
        tostring(generation or 0),
        source_signature(candidate_source),
        tostring(reason)
    }, "|")

    if emitted_status_conflict_by_key[key] then
        return false
    end

    emitted_status_conflict_by_key[key] = true

    return true
end

local function next_status_generation(defender_name, status_key)
    local key = defender_name .. "|" .. status_key
    local next_value =
        (next_status_generation_by_key[key] or 0) + 1

    next_status_generation_by_key[key] = next_value

    return next_value
end

local function emit_status_generation_record(
    now,
    event_name,
    defender_name,
    status_id,
    status_name,
    generation,
    candidate_source,
    frozen_source,
    decision,
    reason
)
    status_generation_record_sequence =
        status_generation_record_sequence + 1

    append_diagnostic_line(string.format(
        "G|%.3f|%d|%s|%s|%s|%s|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        now,
        status_generation_record_sequence,
        sanitize_field(event_name),
        sanitize_field(defender_name),
        sanitize_field(status_id),
        sanitize_field(status_name),
        generation or 0,
        sanitize_field(source_field(candidate_source, "Type")),
        sanitize_field(source_field(candidate_source, "Actor")),
        sanitize_field(source_field(candidate_source, "Name")),
        sanitize_field(source_field(candidate_source, "Label")),
        sanitize_field(source_field(candidate_source, "Identity")),
        sanitize_field(source_field(frozen_source, "Actor")),
        sanitize_field(source_field(frozen_source, "Label")),
        sanitize_field(source_field(frozen_source, "Identity")),
        sanitize_field(decision),
        sanitize_field(reason)
    ))
end

local function emit_status_record(
    now,
    event_name,
    defender_name,
    status_id_text,
    status_name,
    issuer_text,
    invoker_text,
    param_details,
    source,
    match_method
)
    status_event_sequence = status_event_sequence + 1

    local source_age = -1.0

    if source ~= nil and source.Time ~= nil then
        source_age = now - source.Time
    end

    append_diagnostic_line(string.format(
        "S|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%.3f|%s|%s|%s|%s\n",
        now,
        status_event_sequence,
        sanitize_field(event_name),
        sanitize_field(defender_name),
        sanitize_field(status_id_text),
        sanitize_field(status_name),
        sanitize_field(issuer_text),
        sanitize_field(invoker_text),
        sanitize_field(param_details.Index),
        sanitize_field(param_details.Name),
        sanitize_field(param_details.FloatValue),
        sanitize_field(param_details.Instance),
        sanitize_field(param_details.InstancePlayer),
        sanitize_field(param_details.InstanceDebugName),
        sanitize_field(source_field(source, "Type")),
        sanitize_field(source_field(source, "Actor")),
        sanitize_field(source_field(source, "Name")),
        sanitize_field(source_field(source, "Label")),
        source_age,
        sanitize_field(source_field(source, "Identity")),
        sanitize_field(source_field(source, "State")),
        sanitize_field(source_field(source, "ActorInstance")),
        sanitize_field(match_method)
    ))
end

local function get_status_table(defender_name)
    local statuses = active_status_by_defender[defender_name]

    if statuses == nil then
        statuses = {}
        active_status_by_defender[defender_name] = statuses
    end

    return statuses
end

local function handle_status_event(
    context_parameter,
    event_name,
    status_parameter,
    param_parameter,
    issuer_parameter,
    invoker_parameter,
    is_remove
)
    local owner = get_component_owner_object(context_parameter)
    local defender_name = clean_name(object_name(owner))

    if defender_name == "unknown" or
        defender_name == "invalid" or
        defender_name == "nil" then
        return
    end

    local status_id_text,
        status_name,
        numeric_id,
        is_tracked =
        get_status_info(status_parameter)

    if not is_tracked then
        return
    end

    local now = os.clock()
    local issuer_text = diagnostic_value_text(issuer_parameter)
    local invoker_text = format_guid_value(invoker_parameter)
    local param_details =
        get_status_parameter_details(param_parameter)
    local status_key =
        numeric_id ~= nil and tostring(numeric_id) or status_name
    local statuses = get_status_table(defender_name)
    local existing = statuses[status_key]
    local source, match_method =
        resolve_status_source(
            defender_name,
            param_details,
            invoker_text,
            now
        )

    if source == nil and existing ~= nil then
        source = copy_source_snapshot(existing.Source)
        match_method = existing.MatchMethod or "PRIOR_STATUS"
    end

    if is_remove then
        if existing ~= nil then
            issuer_text =
                issuer_text ~= "unknown"
                    and issuer_text
                    or existing.Issuer
            invoker_text =
                invoker_text ~= "unknown"
                    and invoker_text
                    or existing.Invoker
            param_details = existing.Param or param_details
            source = copy_source_snapshot(existing.Source)
            match_method =
                existing.MatchMethod or "PRIOR_STATUS"

            emit_status_generation_record(
                now,
                event_name,
                defender_name,
                status_id_text,
                status_name,
                existing.Generation or 0,
                source,
                source,
                "REMOVE_FROZEN",
                "DIRECT_STATUS_REMOVE"
            )
        end

        emit_status_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            issuer_text,
            invoker_text,
            param_details,
            source,
            match_method
        )

        statuses[status_key] = nil
        return
    end

    local first_applied_time = now
    local duplicate_event =
        existing ~= nil and
        now - (existing.LastAppliedTime or now) <= 0.35
    local generation =
        duplicate_event and existing.Generation or nil

    if duplicate_event then
        first_applied_time =
            existing.FirstAppliedTime or now
    end

    if generation == nil then
        generation = next_status_generation(
            defender_name,
            status_key
        )

        emit_status_generation_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            generation,
            source,
            source,
            "NEW_GENERATION",
            existing ~= nil
                and "DIRECT_STATUS_REAPPLY"
                or "DIRECT_STATUS_ADD"
        )
    end

    local status_state = {
        StatusId = status_id_text,
        StatusName = status_name,
        FirstAppliedTime = first_applied_time,
        LastAppliedTime = now,
        LastObservedTime = now,
        Issuer = issuer_text,
        Invoker = invoker_text,
        Param = param_details,
        Source = source,
        MatchMethod = match_method,
        Generation = generation,
        ObjectDetails = existing ~= nil
            and existing.ObjectDetails
            or nil
    }

    statuses[status_key] = status_state
    last_status_by_defender[defender_name] = status_state

    emit_status_record(
        now,
        event_name,
        defender_name,
        status_id_text,
        status_name,
        issuer_text,
        invoker_text,
        param_details,
        source,
        match_method
    )
end

local function handle_status_remove_invoker(
    context_parameter,
    invoker_parameter
)
    local owner = get_component_owner_object(context_parameter)
    local defender_name = clean_name(object_name(owner))
    local statuses = active_status_by_defender[defender_name]

    if statuses == nil then
        return
    end

    local invoker_text = format_guid_value(invoker_parameter)
    local invoker_key = normalize_identity_key(invoker_text)

    for status_key, status in pairs(statuses) do
        local status_invoker_key =
            normalize_identity_key(status.Invoker)

        if invoker_key ~= nil and
            status_invoker_key == invoker_key then
            emit_status_record(
                os.clock(),
                "REMOVE_INVOKER",
                defender_name,
                status.StatusId,
                status.StatusName,
                status.Issuer,
                invoker_text,
                status.Param,
                status.Source,
                status.MatchMethod or "PRIOR_STATUS"
            )

            statuses[status_key] = nil
        end
    end
end

local function handle_status_remove_all(context_parameter)
    local owner = get_component_owner_object(context_parameter)
    local defender_name = clean_name(object_name(owner))
    local statuses = active_status_by_defender[defender_name]

    if statuses == nil then
        return
    end

    for status_key, status in pairs(statuses) do
        emit_status_record(
            os.clock(),
            "REMOVE_ALL",
            defender_name,
            status.StatusId,
            status.StatusName,
            status.Issuer,
            status.Invoker,
            status.Param,
            status.Source,
            status.MatchMethod or "PRIOR_STATUS"
        )

        statuses[status_key] = nil
    end
end


local function get_status_object_owner(status_object)
    status_object = unwrap_maybe_parameter(status_object)

    if not is_valid_object(status_object) then
        return nil
    end

    local owner = call_object_method(status_object, "GetOwner")

    if is_valid_object(owner) then
        return owner
    end

    return nil
end

local function get_status_object_key(status_object)
    status_object = unwrap_maybe_parameter(status_object)

    if not is_valid_object(status_object) then
        return "unknown"
    end

    return object_name(status_object)
end

local function infer_status_from_object(status_object)
    local combined = (
        object_name(status_object)
        .. " "
        .. get_object_class_name(status_object)
    ):lower():gsub("[^%w]", "")

    local ordered_candidates = {
        { "playerarrowexplosion", 61 },
        { "ivycling", 24 },
        { "falldamage", 17 },
        { "lavadamage", 18 },
        { "electrical", 22 },
        { "darkness", 25 },
        { "wetness", 20 },
        { "freeze", 21 },
        { "poison", 5 },
        { "burn", 19 },
        { "muddy", 23 },
        { "drown", 14 }
    }

    for _, candidate in ipairs(ordered_candidates) do
        if combined:find(candidate[1], 1, true) ~= nil then
            local numeric_id = candidate[2]

            return tostring(numeric_id),
                STATUS_NAMES_BY_ID[numeric_id],
                numeric_id,
                true
        end
    end

    return "unknown", "unknown", nil, false
end

local function get_status_object_info(status_object, status_id_hint)
    status_object = unwrap_maybe_parameter(status_object)

    local status_value = nil

    if is_valid_object(status_object) then
        status_value =
            read_struct_field(status_object, "statusID") or
            read_struct_field(status_object, "StatusID")
    end

    if status_value == nil then
        status_value = unwrap_maybe_parameter(status_id_hint)
    end

    local status_id_text,
        status_name,
        numeric_id,
        is_tracked =
        get_status_info(status_value)

    if not is_tracked and status_id_hint ~= nil then
        status_id_text,
            status_name,
            numeric_id,
            is_tracked =
            get_status_info(status_id_hint)
    end

    if not is_tracked and is_valid_object(status_object) then
        status_id_text,
            status_name,
            numeric_id,
            is_tracked =
            infer_status_from_object(status_object)
    end

    if numeric_id == nil or
        TRACKED_STATUS_ID_SET[numeric_id] ~= true then
        is_tracked = false
    end

    return status_id_text,
        status_name,
        numeric_id,
        is_tracked
end

local function get_status_object_float(status_object, field_name, method_name)
    local value = nil
    local numeric = nil

    if method_name ~= nil then
        value = call_object_method(status_object, method_name)
        numeric = tonumber(value)

        if numeric ~= nil then
            return numeric
        end
    end

    value = read_struct_field(status_object, field_name)
    numeric = tonumber(value)

    return numeric
end

local function get_status_object_bool(status_object, field_name, method_name)
    local value = read_struct_field(status_object, field_name)

    if value == nil and method_name ~= nil then
        value = call_object_method(status_object, method_name)
    end

    value = unwrap_maybe_parameter(value)

    if value == true or value == false then
        return value
    end

    local numeric = tonumber(value)

    if numeric ~= nil then
        return numeric ~= 0
    end

    return false
end

local function get_status_object_details(status_object)
    status_object = unwrap_maybe_parameter(status_object)

    if not is_valid_object(status_object) then
        return {
            Object = "unknown",
            Class = "unknown",
            InstanceGuid = "unknown",
            Duration = -1.0,
            Remaining = -1.0,
            DurationTimer = -1.0,
            IsEnded = false,
            DynamicParameter = nil,
            Param = get_status_parameter_details(nil)
        }
    end

    local dynamic_parameter =
        read_struct_field(status_object, "DynamicParameter")
    local duration =
        get_status_object_float(
            status_object,
            "Duration",
            "GetDuration"
        )
    local remaining =
        get_status_object_float(
            status_object,
            "DurationTimer",
            "GetRemainingTime"
        )
    local duration_timer =
        get_status_object_float(
            status_object,
            "DurationTimer",
            nil
        )

    return {
        Object = clean_name(object_name(status_object)),
        Class = get_object_class_name(status_object),
        InstanceGuid = format_guid_value(
            read_struct_field(status_object, "InstanceGuid")
        ),
        Duration = duration or -1.0,
        Remaining = remaining or -1.0,
        DurationTimer = duration_timer or -1.0,
        IsEnded = get_status_object_bool(
            status_object,
            "bIsEndStatus",
            "IsEndStatus"
        ),
        DynamicParameter = dynamic_parameter,
        Param = get_status_parameter_details(dynamic_parameter)
    }
end

local function emit_replicated_status_record(
    now,
    event_name,
    defender_name,
    status_id_text,
    status_name,
    details,
    source,
    match_method
)
    replicated_status_sequence =
        replicated_status_sequence + 1

    local source_age = -1.0

    if source ~= nil and source.Time ~= nil then
        source_age = now - source.Time
    end

    append_diagnostic_line(string.format(
        "R|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%.3f|%.3f|%.3f|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%.3f|%s|%s\n",
        now,
        replicated_status_sequence,
        sanitize_field(event_name),
        sanitize_field(defender_name),
        sanitize_field(status_id_text),
        sanitize_field(status_name),
        sanitize_field(details.Object),
        sanitize_field(details.Class),
        sanitize_field(details.InstanceGuid),
        details.Duration,
        details.Remaining,
        details.DurationTimer,
        details.IsEnded and "true" or "false",
        sanitize_field(details.Param.Index),
        sanitize_field(details.Param.Name),
        sanitize_field(details.Param.FloatValue),
        sanitize_field(details.Param.Instance),
        sanitize_field(source_field(source, "Type")),
        sanitize_field(source_field(source, "Actor")),
        sanitize_field(source_field(source, "Name")),
        sanitize_field(source_field(source, "Label")),
        sanitize_field(source_field(source, "Identity")),
        sanitize_field(source_field(source, "State")),
        source_age,
        sanitize_field(source_field(source, "ActorInstance")),
        sanitize_field(match_method)
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][STATUS OBJECT] event=%s defender=%s "
        .. "status=%s object=%s source=%s match=%s\n",
        event_name,
        defender_name,
        status_name,
        details.Object,
        source_field(source, "Label"),
        match_method
    ))
end

local function record_status_object(
    event_name,
    status_object,
    status_component,
    status_id_hint,
    is_remove,
    force_emit
)
    status_object = unwrap_maybe_parameter(status_object)
    status_component = unwrap_maybe_parameter(status_component)

    local owner = get_status_object_owner(status_object)

    if not is_valid_object(owner) then
        owner = get_component_owner_object(status_component)
    end

    local defender_name = clean_name(object_name(owner))

    if defender_name == "unknown" or
        defender_name == "invalid" or
        defender_name == "nil" then
        return false
    end

    local status_id_text,
        status_name,
        numeric_id,
        is_tracked =
        get_status_object_info(status_object, status_id_hint)

    if not is_tracked then
        return false
    end

    local now = os.clock()
    local details = get_status_object_details(status_object)
    local status_key = tostring(numeric_id)
    local statuses = get_status_table(defender_name)
    local existing = statuses[status_key]
    local existing_details =
        existing ~= nil and existing.ObjectDetails or nil
    local current_guid_key =
        normalize_identity_key(details.InstanceGuid)
    local existing_guid_key =
        existing_details ~= nil and
        normalize_identity_key(existing_details.InstanceGuid) or nil
    local same_guid =
        current_guid_key ~= nil and
        existing_guid_key ~= nil and
        current_guid_key == existing_guid_key
    local same_recent_unknown_instance =
        existing ~= nil and
        current_guid_key == nil and
        existing_guid_key == nil and
        existing_details ~= nil and
        existing_details.Class == details.Class and
        now - (existing.LastObservedTime or now) <= 1.5 and
        event_name ~= "ONREP_ADD" and
        event_name ~= "BASE_BEGIN" and
        event_name ~= "BASE_BEGIN_SOME"
    local same_status_instance =
        same_guid or
        same_recent_unknown_instance
    local candidate_source, candidate_match_method =
        resolve_status_source(
            defender_name,
            details.Param,
            "unknown",
            now
        )
    local source = copy_source_snapshot(candidate_source)
    local match_method = candidate_match_method

    if same_status_instance and
        existing ~= nil and
        existing.Source ~= nil then
        if candidate_source ~= nil and
            source_signature(candidate_source) ~=
                source_signature(existing.Source) and
            should_emit_status_conflict(
                defender_name,
                status_id_text,
                existing.Generation,
                candidate_source,
                "SAME_INSTANCE_COMPETING_SOURCE"
            ) then
            emit_status_generation_record(
                now,
                event_name,
                defender_name,
                status_id_text,
                status_name,
                existing.Generation or 0,
                candidate_source,
                existing.Source,
                "KEEP_FROZEN",
                "SAME_INSTANCE_COMPETING_SOURCE"
            )
        end

        source = copy_source_snapshot(existing.Source)
        match_method =
            existing.MatchMethod or "PRIOR_STATUS"
    elseif same_status_instance and
        source == nil and
        existing ~= nil then
        source = copy_source_snapshot(existing.Source)
        match_method =
            existing.MatchMethod or "PRIOR_STATUS"
    elseif not same_status_instance and
        source == nil and
        existing ~= nil and
        existing.Source ~= nil then
        source = copy_source_snapshot(existing.Source)
        match_method = "PRIOR_GENERATION_FALLBACK"
    end

    if is_remove then
        if existing ~= nil then
            details =
                existing.ObjectDetails or details
            source = copy_source_snapshot(existing.Source)
            match_method =
                existing.MatchMethod or "PRIOR_STATUS"

            emit_status_generation_record(
                now,
                event_name,
                defender_name,
                status_id_text,
                status_name,
                existing.Generation or 0,
                candidate_source,
                source,
                "REMOVE_FROZEN",
                "PRESERVE_ORIGINAL_STATUS_SOURCE"
            )
        end

        emit_status_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            "unknown",
            "unknown",
            details.Param,
            source,
            match_method
        )

        emit_replicated_status_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            details,
            source,
            match_method
        )

        statuses[status_key] = nil

        return true
    end

    local first_applied_time = now
    local last_applied_time = now

    if same_status_instance and existing ~= nil then
        first_applied_time =
            existing.FirstAppliedTime or now
        last_applied_time =
            existing.LastAppliedTime or first_applied_time

        if event_name == "ONREP_ADD" or
            event_name == "BASE_BEGIN" or
            event_name == "BASE_BEGIN_SOME" then
            last_applied_time = now
        end

        if source == nil then
            source = copy_source_snapshot(existing.Source)
            match_method =
                existing.MatchMethod or match_method
        end
    end

    local generation =
        same_status_instance and
        existing ~= nil and
        existing.Generation or nil

    if generation == nil then
        generation = next_status_generation(
            defender_name,
            status_key
        )

        emit_status_generation_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            generation,
            candidate_source,
            source,
            "NEW_GENERATION",
            existing ~= nil
                and "REPLACED_STATUS_INSTANCE"
                or "FIRST_STATUS_INSTANCE"
        )
    end

    local status_state = {
        StatusId = status_id_text,
        StatusName = status_name,
        FirstAppliedTime = first_applied_time,
        LastAppliedTime = last_applied_time,
        LastObservedTime = now,
        Issuer = "unknown",
        Invoker = "unknown",
        Param = details.Param,
        Source = source,
        MatchMethod = match_method,
        ObjectDetails = details,
        Generation = generation
    }

    statuses[status_key] = status_state
    last_status_by_defender[defender_name] = status_state
    local object_key = get_status_object_key(status_object)
    local should_emit = force_emit == true

    if object_key ~= "unknown" then
        if not first_seen_status_object[object_key] then
            first_seen_status_object[object_key] = true
            should_emit = true
        end

        local previous_snapshot =
            last_status_snapshot_time_by_object[object_key]

        if previous_snapshot == nil or
            now - previous_snapshot >= 1.0 then
            should_emit = true
            last_status_snapshot_time_by_object[object_key] =
                now
        end
    end

    if should_emit then
        emit_status_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            "unknown",
            "unknown",
            details.Param,
            source,
            match_method
        )

        emit_replicated_status_record(
            now,
            event_name,
            defender_name,
            status_id_text,
            status_name,
            details,
            source,
            match_method
        )
    end

    return true
end

local function remove_status_object_by_id(
    event_name,
    status_component,
    status_id_parameter
)
    status_component = unwrap_maybe_parameter(status_component)

    local owner = get_component_owner_object(status_component)
    local defender_name = clean_name(object_name(owner))
    local status_id_text,
        status_name,
        numeric_id,
        is_tracked =
        get_status_info(status_id_parameter)

    if defender_name == "unknown" or
        defender_name == "invalid" or
        defender_name == "nil" or
        not is_tracked then
        return false
    end

    local status_key = tostring(numeric_id)
    local statuses = active_status_by_defender[defender_name]
    local existing =
        statuses ~= nil and statuses[status_key] or nil

    if existing == nil then
        return false
    end

    local now = os.clock()
    local details =
        existing.ObjectDetails or get_status_object_details(nil)

    emit_status_record(
        now,
        event_name,
        defender_name,
        status_id_text,
        status_name,
        existing.Issuer or "unknown",
        existing.Invoker or "unknown",
        existing.Param or details.Param,
        existing.Source,
        existing.MatchMethod or "PRIOR_STATUS"
    )

    emit_replicated_status_record(
        now,
        event_name,
        defender_name,
        status_id_text,
        status_name,
        details,
        existing.Source,
        existing.MatchMethod or "PRIOR_STATUS"
    )

    statuses[status_key] = nil
    return true
end

local function scan_replicated_statuses(context_parameter)
    local component = get_value(context_parameter)

    if component == nil then
        component = unwrap_maybe_parameter(context_parameter)
    end

    if not is_valid_object(component) then
        return
    end

    local component_key = object_name(component)
    local owner = get_component_owner_object(component)
    local defender_name = clean_name(object_name(owner))
    local previous =
        active_status_ids_by_component[component_key] or {}
    local current = {}
    local active_count = 0
    local query_failures = 0
    local first_error = "none"

    for _, numeric_id in ipairs(TRACKED_STATUS_ID_LIST) do
        local ok, status_object = pcall(function()
            return component:GetExecutionStatus(numeric_id)
        end)

        if not ok then
            query_failures = query_failures + 1

            if first_error == "none" then
                first_error = sanitize_field(tostring(status_object))
            end
        end

        status_object = unwrap_maybe_parameter(status_object)

        if ok and is_valid_object(status_object) then
            active_count = active_count + 1

            local status_key = tostring(numeric_id)
            local object_key =
                get_status_object_key(status_object)

            current[status_key] = object_key

            local event_name = nil

            if previous[status_key] == nil then
                event_name = "ONREP_ADD"
            elseif previous[status_key] ~= object_key then
                event_name = "ONREP_REPLACE"
            end

            record_status_object(
                event_name or "ONREP_REFRESH",
                status_object,
                component,
                numeric_id,
                false,
                event_name ~= nil
            )
        end
    end

    for status_key, _ in pairs(previous) do
        if current[status_key] == nil then
            remove_status_object_by_id(
                "ONREP_REMOVE",
                component,
                tonumber(status_key) or status_key
            )
        end
    end

    active_status_ids_by_component[component_key] =
        current

    verbose_print(string.format(
        "[RogueModeTelemetry][STATUS SCAN] defender=%s "
        .. "active=%d failures=%d first_error=%s\n",
        defender_name,
        active_count,
        query_failures,
        first_error
    ))
end

local function handle_status_base_event(
    event_name,
    context_parameter,
    is_remove,
    force_emit
)
    local status_object = get_value(context_parameter)

    if status_object == nil then
        status_object =
            unwrap_maybe_parameter(context_parameter)
    end

    record_status_object(
        event_name,
        status_object,
        nil,
        nil,
        is_remove,
        force_emit
    )
end

local function refresh_recent_status_source(
    defender_name,
    source,
    now
)
    local statuses = active_status_by_defender[defender_name]

    if statuses == nil then
        return
    end

    for _, status in pairs(statuses) do
        local application_age =
            now - (status.LastAppliedTime or now)

        if application_age >= 0 and application_age <= 0.75 then
            local should_update =
                status.Source == nil or
                status.MatchMethod == "UNRESOLVED"

            if should_update then
                status.Source = copy_source_snapshot(source)
                status.MatchMethod = "POST_HIT_UPDATE"

                emit_status_generation_record(
                    now,
                    "SOURCE_UPDATE",
                    defender_name,
                    status.StatusId,
                    status.StatusName,
                    status.Generation or 0,
                    source,
                    status.Source,
                    "RESOLVE_SOURCE",
                    "PREVIOUSLY_UNRESOLVED"
                )

                emit_status_record(
                    now,
                    "SOURCE_UPDATE",
                    defender_name,
                    status.StatusId,
                    status.StatusName,
                    status.Issuer,
                    status.Invoker,
                    status.Param,
                    status.Source,
                    status.MatchMethod
                )
            elseif status.Source ~= nil and
                source_signature(source) ~=
                    source_signature(status.Source) and
                should_emit_status_conflict(
                    defender_name,
                    status.StatusId,
                    status.Generation,
                    source,
                    "LATER_HIT_DURING_ACTIVE_STATUS"
                ) then
                emit_status_generation_record(
                    now,
                    "SOURCE_CONFLICT",
                    defender_name,
                    status.StatusId,
                    status.StatusName,
                    status.Generation or 0,
                    source,
                    status.Source,
                    "KEEP_FROZEN",
                    "LATER_HIT_DURING_ACTIVE_STATUS"
                )
            end
        end
    end
end

local function select_status_for_slip(defender_name)
    local statuses = active_status_by_defender[defender_name]
    local selected = nil
    local active_names = {}

    if statuses ~= nil then
        for _, status in pairs(statuses) do
            active_names[#active_names + 1] =
                status.StatusName or "unknown"

            if selected == nil or
                (status.LastAppliedTime or 0) >
                    (selected.LastAppliedTime or 0) then
                selected = status
            end
        end
    end

    table.sort(active_names)

    if selected == nil then
        local recent = last_status_by_defender[defender_name]

        if recent ~= nil and
            os.clock() - (recent.LastAppliedTime or 0) <= 60.0 then
            selected = recent
        end
    end

    return selected,
        #active_names,
        table.concat(active_names, ",")
end

local function emit_slip_damage_diagnostic(
    context_parameter,
    damage_result_parameter
)
    local damage_result = get_value(damage_result_parameter)

    if damage_result == nil then
        return
    end

    local actual_damage =
        tonumber(read_struct_field(damage_result, "ActualDamage")) or 0
    local raw_damage =
        tonumber(read_struct_field(damage_result, "Damage")) or 0

    if actual_damage <= 0 and raw_damage <= 0 then
        return
    end

    local defender =
        read_struct_field(damage_result, "Defender")

    if not is_valid_object(defender) then
        defender = get_component_owner_object(context_parameter)
    end

    local defender_name = clean_name(object_name(defender))
    local now = os.clock()
    local status,
        active_status_count,
        active_status_names =
        select_status_for_slip(defender_name)

    local source = status ~= nil and status.Source or nil
    local status_age = -1.0
    local source_age = -1.0

    if status ~= nil and status.FirstAppliedTime ~= nil then
        status_age = now - status.FirstAppliedTime
    end

    if source ~= nil and source.Time ~= nil then
        source_age = now - source.Time
    end

    slip_damage_sequence = slip_damage_sequence + 1

    append_line(string.format(
        "T|%.3f|%d|%s|%d|%d|%s|%s|%s|%s|%s|%s|%s|%.3f|%.3f|%s|%s|%s|%s|%s|%d|%s|%d\n",
        now,
        slip_damage_sequence,
        sanitize_field(defender_name),
        actual_damage,
        raw_damage,
        sanitize_field(
            status ~= nil and status.StatusId or "unknown"
        ),
        sanitize_field(
            status ~= nil and status.StatusName or "unknown"
        ),
        sanitize_field(source_field(source, "Type")),
        sanitize_field(source_field(source, "Actor")),
        sanitize_field(source_field(source, "Name")),
        sanitize_field(source_field(source, "Label")),
        sanitize_field(source_field(source, "Identity")),
        status_age,
        source_age,
        sanitize_field(
            status ~= nil and status.Issuer or "unknown"
        ),
        sanitize_field(
            status ~= nil and status.Invoker or "unknown"
        ),
        sanitize_field(
            status ~= nil and
                status.Param.Instance or
                "unknown"
        ),
        sanitize_field(source_field(source, "ActorInstance")),
        sanitize_field(
            status ~= nil and
                status.MatchMethod or
                "NO_STATUS"
        ),
        active_status_count,
        sanitize_field(active_status_names),
        status ~= nil and (status.Generation or 0) or 0
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][SLIP] defender=%s damage=%d "
        .. "status=%s source=%s match=%s\n",
        defender_name,
        actual_damage,
        status ~= nil and status.StatusName or "unknown",
        source_field(source, "Label"),
        status ~= nil and
            status.MatchMethod or
            "NO_STATUS"
    ))
end


local function get_partner_skill_owner(component)
    component = unwrap_maybe_parameter(component)

    if not is_valid_object(component) then
        return nil
    end

    local owner = call_object_method(component, "GetOwner")

    if is_valid_object(owner) then
        return owner
    end

    owner = read_struct_field(component, "Owner")

    if is_valid_object(owner) then
        return owner
    end

    return nil
end

local function get_pal_trainer_info(pal_actor)
    pal_actor = unwrap_maybe_parameter(pal_actor)

    if not is_valid_object(pal_actor) then
        return nil, "unknown", "unknown"
    end

    local parameter_component =
        get_character_parameter_component(pal_actor)

    if parameter_component == nil then
        return nil, "unknown", "unknown"
    end

    local ok, trainer = pcall(function()
        return parameter_component.Trainer
    end)

    trainer = unwrap_maybe_parameter(trainer)

    if not ok or not is_valid_object(trainer) then
        return nil, "unknown", "unknown"
    end

    local trainer_actor = clean_name(object_name(trainer))
    local trainer_name =
        get_player_display_name(trainer) or
        trainer_actor

    return trainer, trainer_actor, trainer_name
end

local function get_pal_character_id(pal_actor)
    local parameter_component =
        get_character_parameter_component(pal_actor)

    if parameter_component == nil then
        return nil
    end

    local individual,
        character_id,
        unique_npc_id,
        save_parameter =
        get_pal_identity(parameter_component)

    return character_id
end

local function resolve_partner_skill_display_name(
    world_context,
    character_id
)
    character_id = unwrap_maybe_parameter(character_id)

    if character_id == nil then
        return nil
    end

    local character_key = diagnostic_value_text(character_id)
    local cached =
        resolved_partner_skill_name_by_character_id[
            character_key
        ]

    if cached ~= nil then
        return cached
    end

    local utility = get_pal_ui_utility()

    if utility == nil then
        return nil
    end

    world_context = unwrap_maybe_parameter(world_context)

    if not is_valid_object(world_context) then
        return nil
    end

    local out_parameters = {}

    local ok, return_value = pcall(function()
        return utility:GetPartnerSkillName(
            world_context,
            character_id,
            out_parameters
        )
    end)

    if not ok then
        return nil
    end

    local resolved_name = meaningful_name(
        out_parameters.OutText or
        out_parameters.outText or
        out_parameters.OutName or
        out_parameters.outName or
        out_parameters[1]
    )

    if resolved_name == nil then
        resolved_name = meaningful_name(return_value)
    end

    if resolved_name ~= nil then
        resolved_partner_skill_name_by_character_id[
            character_key
        ] = resolved_name
    end

    return resolved_name
end

local function make_partner_character_id_candidates(
    internal_name
)
    internal_name = tostring(internal_name or "")

    local base_name = internal_name:match(
        "^(.-)_PartnerSkill_"
    )

    if base_name == nil or base_name == "" then
        return {}
    end

    local candidates = {
        base_name,
        base_name .. "_BOSS",
        "BP_" .. base_name,
        "BP_" .. base_name .. "_BOSS"
    }

    local unique = {}
    local output = {}

    for _, candidate in ipairs(candidates) do
        if not unique[candidate] then
            unique[candidate] = true
            table.insert(output, candidate)
        end
    end

    return output
end

local function humanize_partner_character_id(character_id)
    local value = tostring(character_id or "Partner Pal")

    value = value:gsub("^BP_", "")
    value = value:gsub("_BOSS$", "")
    value = value:gsub("_", " ")
    value = value:gsub("(%l)(%u)", "%1 %2")

    return value
end

local function resolve_partner_name_from_internal(
    world_context,
    internal_name
)
    local candidates =
        make_partner_character_id_candidates(internal_name)

    for _, candidate in ipairs(candidates) do
        local name_value = candidate

        local ok_fname, converted = pcall(function()
            return FName(candidate)
        end)

        if ok_fname and converted ~= nil then
            name_value = converted
        end

        local resolved =
            resolve_partner_skill_display_name(
                world_context,
                name_value
            )

        local probe_key =
            tostring(internal_name) .. "|" .. candidate

        if not emitted_partner_name_probe_by_key[probe_key] then
            emitted_partner_name_probe_by_key[probe_key] = true
            partner_name_probe_sequence =
                partner_name_probe_sequence + 1

            append_diagnostic_line(string.format(
                "Y|%.3f|%d|%s|%s|%s|%s\n",
                os.clock(),
                partner_name_probe_sequence,
                sanitize_field(internal_name),
                sanitize_field(candidate),
                sanitize_field(resolved or "unresolved"),
                sanitize_field(
                    resolved ~= nil and "RESOLVED" or "FAILED"
                )
            ))
        end

        if resolved ~= nil then
            return resolved, candidate
        end
    end

    local fallback_id =
        candidates[1] or tostring(internal_name or "unknown")

    return nil, fallback_id
end

local function get_partner_component_value(
    component,
    method_name,
    field_name
)
    local value = call_object_method(component, method_name)

    if value ~= nil then
        return value
    end

    return read_struct_field(component, field_name)
end

local function copy_partner_skill_snapshot(skill)
    if skill == nil then
        return nil
    end

    return {
        PalActor = skill.PalActor,
        PalName = skill.PalName,
        PlayerActor = skill.PlayerActor,
        PlayerName = skill.PlayerName,
        CharacterId = skill.CharacterId,
        InternalName = skill.InternalName,
        DisplayName = skill.DisplayName,
        WazaId = skill.WazaId,
        WazaName = skill.WazaName,
        Running = skill.Running,
        Event = skill.Event,
        EventTime = skill.EventTime,
        EffectTime = skill.EffectTime,
        EffectTimeMax = skill.EffectTimeMax,
        BuffSeenTime = skill.BuffSeenTime,
        BuffObject = skill.BuffObject
    }
end

local function store_partner_skill_for_player(skill_state)
    if skill_state == nil or
        skill_state.PlayerActor == nil or
        skill_state.PlayerActor == "unknown" then
        return
    end

    local player_skills =
        partner_skills_by_player_actor[
            skill_state.PlayerActor
        ]

    if player_skills == nil then
        player_skills = {}
        partner_skills_by_player_actor[
            skill_state.PlayerActor
        ] = player_skills
    end

    local key = skill_state.CharacterId

    if key == nil or key == "unknown" then
        key = skill_state.PalActor
    end

    player_skills[key] = skill_state
end

local function find_lantern_enchantment_for_player(
    player_actor
)
    local player_skills =
        partner_skills_by_player_actor[player_actor]

    if player_skills == nil then
        return nil
    end

    for _, skill in pairs(player_skills) do
        local character_id =
            tostring(skill.CharacterId or "")
        local display_name =
            tostring(skill.DisplayName or "")
        local pal_name =
            tostring(skill.PalName or "")

        if character_id:find(
                "LanternButler",
                1,
                true) ~= nil or
            display_name == "Lantern Enchantment" or
            pal_name == "Loomen" then
            return skill
        end
    end

    return nil
end

local function emit_partner_skill_event(
    event_name,
    context_parameter,
    effect_time_parameter,
    effect_time_max_parameter
)
    local component = get_value(context_parameter)

    if not is_valid_object(component) then
        component = unwrap_maybe_parameter(context_parameter)
    end

    if not is_valid_object(component) then
        return
    end

    local pal_actor = get_partner_skill_owner(component)

    if not is_valid_object(pal_actor) then
        return
    end

    local pal_actor_name =
        clean_name(object_name(pal_actor))
    local pal_name =
        get_pal_name_info(pal_actor, pal_actor_name)

    remember_known_pal_species(
        pal_actor_name,
        pal_name
    )

    local trainer,
        player_actor_name,
        player_display_name =
        get_pal_trainer_info(pal_actor)

    local character_id =
        get_pal_character_id(pal_actor)
    local character_id_text =
        diagnostic_value_text(character_id)

    local internal_skill_name =
        diagnostic_value_text(
            get_partner_component_value(
                component,
                "GetSkillName",
                "SkillName"
            )
        )

    local display_skill_name =
        resolve_partner_skill_display_name(
            pal_actor,
            character_id
        ) or "unresolved"

    local waza_value =
        get_partner_component_value(
            component,
            "GetWazaID",
            "WazaID"
        )
    local waza_id = get_waza_numeric_id(waza_value)
    local waza_id_text =
        waza_id ~= nil
            and tostring(waza_id)
            or diagnostic_value_text(waza_value)
    local waza_name =
        waza_value ~= nil
            and (
                resolve_waza_name(pal_actor, waza_value) or
                "unresolved"
            )
            or "unresolved"

    local running_value =
        get_partner_component_value(
            component,
            "IsRunning",
            "bIsRunning"
        )
    local running =
        diagnostic_bool_text(running_value)

    local effect_time =
        unwrap_maybe_parameter(effect_time_parameter)

    if effect_time == nil then
        effect_time =
            get_partner_component_value(
                component,
                "GetEffectTime",
                "EffectTime"
            )
    end

    local effect_time_max =
        unwrap_maybe_parameter(effect_time_max_parameter)

    if effect_time_max == nil then
        effect_time_max =
            get_partner_component_value(
                component,
                "GetEffectTimeMax",
                "EffectTimeMax"
            )
    end

    local now = os.clock()
    local skill_state = {
        PalActor = pal_actor_name,
        PalName = pal_name,
        PlayerActor = player_actor_name,
        PlayerName = player_display_name,
        CharacterId = character_id_text,
        InternalName = internal_skill_name,
        DisplayName = display_skill_name,
        WazaId = waza_id_text,
        WazaName = waza_name,
        Running = running,
        Event = event_name,
        EventTime = now,
        EffectTime = diagnostic_value_text(effect_time),
        EffectTimeMax =
            diagnostic_value_text(effect_time_max)
    }

    if internal_skill_name ~= "unknown" then
        partner_skill_by_internal_name[
            internal_skill_name
        ] = skill_state
    end

    if player_actor_name ~= "unknown" then
        active_partner_skill_by_player_actor[
            player_actor_name
        ] = skill_state
        store_partner_skill_for_player(skill_state)
    end

    partner_skill_sequence =
        partner_skill_sequence + 1

    append_diagnostic_line(string.format(
        "K|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        now,
        partner_skill_sequence,
        sanitize_field(event_name),
        sanitize_field(pal_actor_name),
        sanitize_field(pal_name),
        sanitize_field(player_actor_name),
        sanitize_field(player_display_name),
        sanitize_field(character_id_text),
        sanitize_field(internal_skill_name),
        sanitize_field(display_skill_name),
        sanitize_field(waza_id_text),
        sanitize_field(waza_name),
        sanitize_field(running),
        sanitize_field(skill_state.EffectTime),
        sanitize_field(skill_state.EffectTimeMax),
        sanitize_field(clean_name(object_name(component)))
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][PARTNER %s] pal=%s "
        .. "player=%s internal=%s display=%s "
        .. "waza=%s/%s running=%s\n",
        event_name,
        pal_name,
        player_display_name,
        internal_skill_name,
        display_skill_name,
        waza_id_text,
        waza_name,
        running
    ))
end

local function get_partner_status_info(status_object)
    status_object = unwrap_maybe_parameter(status_object)

    if not is_valid_object(status_object) then
        return nil
    end

    local status_id_text,
        status_name,
        numeric_id,
        is_tracked =
        get_status_object_info(status_object, nil)

    if numeric_id ~= 61 then
        return nil
    end

    local owner = get_status_object_owner(status_object)

    if not is_valid_object(owner) then
        return nil
    end

    local details = get_status_object_details(status_object)
    local internal_name =
        diagnostic_value_text(details.Param.GeneralName)
    local player_actor =
        clean_name(object_name(owner))
    local player_name =
        get_player_display_name(owner) or
        player_actor
    local known_skill =
        partner_skill_by_internal_name[internal_name]

    if known_skill == nil then
        local resolved_display_name,
            inferred_character_id =
            resolve_partner_name_from_internal(
                owner,
                internal_name
            )

        known_skill = {
            PalActor = "unknown",
            PalName = humanize_partner_character_id(
                inferred_character_id
            ),
            PlayerActor = player_actor,
            PlayerName = player_name,
            CharacterId = inferred_character_id,
            InternalName = internal_name,
            DisplayName =
                resolved_display_name or
                humanize_partner_character_id(
                    inferred_character_id
                ) .. " Partner Skill",
            WazaId = "unknown",
            WazaName = "unknown",
            Running = "true",
            Event = "STATUS_BOOTSTRAP",
            EventTime = os.clock(),
            EffectTime = "unknown",
            EffectTimeMax = "unknown"
        }

        partner_skill_by_internal_name[internal_name] =
            known_skill
    end

    return {
        StatusObject = status_object,
        StatusObjectName =
            clean_name(object_name(status_object)),
        StatusClass = details.Class,
        InstanceGuid = details.InstanceGuid,
        ParameterInstance =
            details.Param.InstanceId,
        InternalName = internal_name,
        DurationValue =
            details.Param.GeneralFloatValue,
        PlayerActor = player_actor,
        PlayerName = player_name,
        Skill = copy_partner_skill_snapshot(known_skill)
    }
end

local function emit_partner_buff_status(
    event_name,
    context_parameter,
    force_emit
)
    local status_object = get_value(context_parameter)

    if not is_valid_object(status_object) then
        status_object =
            unwrap_maybe_parameter(context_parameter)
    end

    local info = get_partner_status_info(status_object)

    if info == nil then
        return
    end

    local now = os.clock()
    local object_key =
        get_status_object_key(status_object)
    local should_emit = force_emit == true

    if not seen_partner_status_object[object_key] then
        seen_partner_status_object[object_key] = true
        should_emit = true
    end

    local last_emit =
        last_partner_status_emit_by_object[object_key]

    if event_name == "BASE_TICK" then
        if last_emit == nil or now - last_emit >= 10.0 then
            should_emit = true
        end
    end

    if not should_emit then
        return
    end

    last_partner_status_emit_by_object[object_key] = now

    local skill = info.Skill

    if skill ~= nil and
        info.PlayerActor ~= "unknown" then
        skill.BuffSeenTime = now
        skill.BuffObject = info.StatusObjectName
        partner_buff_seen_by_player[
            info.PlayerActor
        ] = now

        if event_name == "BASE_END" or
            event_name == "BASE_BREAK" or
            event_name == "SCAN_REMOVED" then
            active_partner_skill_by_player_actor[
                info.PlayerActor
            ] = nil
        else
            active_partner_skill_by_player_actor[
                info.PlayerActor
            ] = skill
        end
    end

    partner_buff_sequence =
        partner_buff_sequence + 1

    append_diagnostic_line(string.format(
        "U|%.3f|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n",
        now,
        partner_buff_sequence,
        sanitize_field(event_name),
        sanitize_field(info.PlayerActor),
        sanitize_field(info.PlayerName),
        sanitize_field(info.InternalName),
        sanitize_field(info.StatusObjectName),
        sanitize_field(info.StatusClass),
        sanitize_field(info.InstanceGuid),
        sanitize_field(info.ParameterInstance),
        sanitize_field(info.DurationValue),
        sanitize_field(
            skill ~= nil and skill.PalActor or "unknown"
        ),
        sanitize_field(
            skill ~= nil and skill.PalName or "unknown"
        ),
        sanitize_field(
            skill ~= nil and skill.DisplayName or "unknown"
        ),
        sanitize_field(
            skill ~= nil and skill.WazaId or "unknown"
        ),
        sanitize_field(
            skill ~= nil and skill.WazaName or "unknown"
        )
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][PARTNER BUFF %s] "
        .. "player=%s internal=%s pal=%s display=%s\n",
        event_name,
        info.PlayerName,
        info.InternalName,
        skill ~= nil and skill.PalName or "unknown",
        skill ~= nil and skill.DisplayName or "unknown"
    ))
end

local function partner_component_matches_player(
    component,
    player_actor_name
)
    component = unwrap_maybe_parameter(component)

    if not is_valid_object(component) then
        return false
    end

    local pal_actor = get_partner_skill_owner(component)

    if not is_valid_object(pal_actor) then
        return false
    end

    local _, trainer_actor_name = get_pal_trainer_info(pal_actor)

    return trainer_actor_name == player_actor_name
end

local function find_all_partner_objects(class_name)
    local ok, objects = pcall(function()
        return FindAllOf(class_name)
    end)

    if not ok then
        return nil, tostring(objects)
    end

    if objects == nil then
        return {}, nil
    end

    return objects, nil
end

bootstrap_partner_runtime = function(
    player_actor_name,
    now,
    force_scan
)
    player_actor_name = tostring(
        player_actor_name or "unknown"
    )

    local last_time =
        last_partner_bootstrap_time_by_player[
            player_actor_name
        ]

    if not force_scan and
        last_time ~= nil and
        now - last_time < 2.0 then
        return
    end

    last_partner_bootstrap_time_by_player[
        player_actor_name
    ] = now

    partner_skills_by_player_actor[
        player_actor_name
    ] = {}

    local components, component_error =
        find_all_partner_objects(
            "PalPartnerSkillParameterComponent"
        )

    local scanned_component_count = 0
    local matched_component_count = 0

    for _, component in pairs(components or {}) do
        if is_valid_object(component) then
            scanned_component_count = scanned_component_count + 1

            if partner_component_matches_player(
                    component,
                    player_actor_name) then
                matched_component_count = matched_component_count + 1
                emit_partner_skill_event(
                    "RUNTIME_SCAN",
                    component,
                    nil,
                    nil
                )
            end
        end
    end

    local active =
        active_partner_skill_by_player_actor[
            player_actor_name
        ]
    local last_seen =
        partner_buff_seen_by_player[
            player_actor_name
        ]

    if active ~= nil and
        last_seen ~= nil and
        now - last_seen > 2.5 then
        active_partner_skill_by_player_actor[
            player_actor_name
        ] = nil
    end

    partner_bootstrap_sequence =
        partner_bootstrap_sequence + 1

    append_diagnostic_line(string.format(
        "J|%.3f|%d|%s|%d|%d|%s|%s\n",
        now,
        partner_bootstrap_sequence,
        sanitize_field(player_actor_name),
        scanned_component_count,
        matched_component_count,
        sanitize_field(component_error or "none"),
        "LOCAL_PLAYER_FILTER"
    ))

    verbose_print(string.format(
        "[RogueModeTelemetry][PARTNER BOOTSTRAP] "
        .. "player=%s scanned=%d matched=%d\n",
        player_actor_name,
        scanned_component_count,
        matched_component_count
    ))
end

local function emit_partner_bonus_probe(
    now,
    player_actor,
    player_name,
    defender_name,
    actual_damage,
    weapon_label,
    weapon_identity,
    base_power,
    body_part,
    metadata_weapon_type
)
    local previous =
        last_primary_player_hit_by_actor[player_actor]
    local metadata_type_text =
        tostring(metadata_weapon_type or "unknown")
    local is_paired_secondary =
        metadata_type_text == "0" and
        previous ~= nil and
        now - previous.Time >= 0 and
        now - previous.Time <= 0.35
    local partner =
        is_paired_secondary
            and find_lantern_enchantment_for_player(
                player_actor
            )
            or nil
    local pair_delay = -1.0
    local primary_damage = 0
    local primary_power = 0
    local ratio = 0.0

    if previous ~= nil then
        pair_delay = now - previous.Time
        primary_damage = previous.Damage or 0
        primary_power = previous.BasePower or 0

        if primary_damage > 0 then
            ratio = actual_damage / primary_damage
        end
    end

    local classification = "PLAYER_PRIMARY_OR_OTHER"

    if metadata_type_text == "0" and
        previous ~= nil and
        pair_delay >= 0 and
        pair_delay <= 0.35 then
        if partner ~= nil then
            classification = "PARTNER_BONUS_CONFIRMED"
        else
            classification =
                "UNMAPPED_SECONDARY_CANDIDATE"
        end
    else
        classification = "PLAYER_PRIMARY_OR_OTHER"
    end

    if metadata_type_text ~= "0" and
        weapon_label ~= "Drone Launcher" then
        last_primary_player_hit_by_actor[player_actor] = {
            Time = now,
            Damage = actual_damage,
            BasePower = base_power,
            WeaponLabel = weapon_label,
            WeaponIdentity = weapon_identity,
            Defender = defender_name
        }
    end

    if classification ~= "PARTNER_BONUS_CONFIRMED" and
        classification ~= "PARTNER_BONUS_CANDIDATE" then
        return
    end

    partner_bonus_sequence =
        partner_bonus_sequence + 1

    append_line(string.format(
        "B|%.3f|%d|%s|%s|%s|%d|%d|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%.3f|%d|%d|%.3f|%.4f|%s\n",
        now,
        partner_bonus_sequence,
        sanitize_field(player_actor),
        sanitize_field(player_name),
        sanitize_field(defender_name),
        actual_damage,
        base_power,
        sanitize_field(body_part),
        sanitize_field(metadata_type_text),
        sanitize_field(weapon_label),
        sanitize_field(weapon_identity),
        sanitize_field(
            partner ~= nil and partner.PalActor or "unknown"
        ),
        sanitize_field(
            partner ~= nil and partner.PalName or "unknown"
        ),
        sanitize_field(
            partner ~= nil
                and partner.InternalName
                or "unknown"
        ),
        sanitize_field(
            partner ~= nil
                and partner.DisplayName
                or "unknown"
        ),
        sanitize_field(
            partner ~= nil and partner.WazaId or "unknown"
        ),
        sanitize_field(
            partner ~= nil and partner.WazaName or "unknown"
        ),
        sanitize_field(
            partner ~= nil and partner.Running or "unknown"
        ),
        sanitize_field(
            partner ~= nil and partner.Event or "unknown"
        ),
        partner ~= nil
            and now - (partner.EventTime or now)
            or -1.0,
        primary_damage,
        primary_power,
        pair_delay,
        ratio,
        sanitize_field(classification)
    ))
end

local function resolve_known_player_auxiliary_source(
    base_power,
    metadata_weapon_type
)
    local numeric_weapon_type =
        tonumber(metadata_weapon_type) or -1

    -- Confirmed from the v0.9.0 RC1 regression:
    -- Drone Launcher produces rapid player-owned damage with BasePower 240
    -- and DamageResult WeaponType 3, but does not pass through the normal
    -- PalShooterComponent weapon hooks.
    if base_power == 240 and numeric_weapon_type == 3 then
        return {
            Label = "Drone Launcher",
            Identity = "DroneLauncher_BasePower240",
            State = "AUXILIARY_CONFIRMED"
        }
    end

    return nil
end

local function emit_source_correlation(
    now,
    source_type,
    source_name,
    source_actor,
    source_actor_name,
    defender_name,
    actual_damage,
    base_power,
    body_part,
    metadata_weapon_type
)
    if actual_damage <= 0 then
        return
    end

    local label = "Unresolved"
    local identity = "unknown"
    local state = "UNKNOWN"
    local age_seconds = -1.0

    if source_type == "PAL" or
        source_type == "RAID_PAL" then
        local action =
            active_action_by_actor[source_actor_name]

        if action ~= nil then
            label = action.WazaName or "Unresolved"
            identity = action.WazaId or "unknown"

            if action.EndTime == nil then
                state = "ACTIVE"
                age_seconds =
                    now - (action.StartTime or now)
            else
                state = "AFTER_" .. (action.Phase or "END")
                age_seconds = now - action.EndTime

                -- Ignore stale action identities after a short projectile /
                -- lingering-field grace period.
                if age_seconds > 3.0 then
                    label = "Unresolved"
                    identity = "unknown"
                    state = "STALE"
                end
            end
        end
    elseif source_type == "PLAYER" then
        local auxiliary =
            resolve_known_player_auxiliary_source(
                base_power,
                metadata_weapon_type
            )

        if auxiliary ~= nil then
            label = auxiliary.Label
            identity = auxiliary.Identity
            state = auxiliary.State
            age_seconds = 0.0
        else
            local weapon =
                recent_weapon_by_owner_actor[source_actor_name]

            if weapon ~= nil then
                label =
                    weapon.ItemName ~= "unresolved"
                        and weapon.ItemName
                        or weapon.WeaponActor
                identity =
                    weapon.StaticItemId ~= "unknown"
                        and weapon.StaticItemId
                        or weapon.WeaponClass
                state = weapon.Event or "WEAPON"
                age_seconds =
                    now - (weapon.EventTime or now)

                if age_seconds > 3.0 then
                    label = "Unresolved"
                    identity = "unknown"
                    state = "STALE"
                end
            end
        end

        emit_partner_bonus_probe(
            now,
            source_actor_name,
            source_name,
            defender_name,
            actual_damage,
            label,
            identity,
            base_power,
            body_part,
            metadata_weapon_type
        )
    else
        return
    end

    local actor_instance =
        get_actor_instance_details(source_actor)
    local source_snapshot = {
        Type = source_type,
        Actor = source_actor_name,
        Name = source_name,
        Label = label,
        Identity = identity,
        State = state,
        Time = now,
        ActorInstance = actor_instance.Instance,
        ActorPlayer = actor_instance.Player
    }

    last_friendly_source_by_defender[defender_name] =
        source_snapshot

    local source_instance_key =
        normalize_identity_key(actor_instance.Instance)

    if source_instance_key ~= nil then
        friendly_source_by_instance_id[source_instance_key] =
            source_snapshot
    end

    refresh_recent_status_source(
        defender_name,
        source_snapshot,
        now
    )

    source_correlation_sequence =
        source_correlation_sequence + 1

    append_line(string.format(
        "C|%.3f|%d|%s|%s|%s|%s|%d|%s|%s|%s|%.3f|%d|%s|%s\n",
        now,
        source_correlation_sequence,
        sanitize_field(source_type),
        sanitize_field(source_actor_name),
        sanitize_field(source_name),
        sanitize_field(defender_name),
        actual_damage,
        sanitize_field(label),
        sanitize_field(identity),
        sanitize_field(state),
        age_seconds,
        base_power,
        sanitize_field(body_part),
        sanitize_field(metadata_weapon_type)
    ))
end

local function emit_damage_metadata(damage_result_parameter, hook_name)
    local damage_result = get_value(damage_result_parameter)

    if damage_result == nil then
        return
    end

    local actual_damage =
        tonumber(read_struct_field(damage_result, "ActualDamage")) or 0
    local raw_damage =
        tonumber(read_struct_field(damage_result, "Damage")) or 0

    -- Zero-value calls are initialization/noise rather than combat hits.
    if actual_damage <= 0 and raw_damage <= 0 then
        return
    end

    local raw_attacker =
        read_struct_field(damage_result, "Attacker")
    local defender =
        read_struct_field(damage_result, "Defender")

    local raw_attacker_name =
        clean_name(object_name(raw_attacker))
    local defender_name =
        clean_name(object_name(defender))

    local source_type,
        source_name,
        source_actor,
        resolved_source_actor_name,
        source_name_priority =
        get_source_metadata(raw_attacker)

    source_type,
        source_name,
        source_name_priority =
        apply_raid_pal_fallback(
            source_type,
            source_name,
            source_actor,
            resolved_source_actor_name,
            source_name_priority,
            defender_name
        )

    damage_metadata_sequence = damage_metadata_sequence + 1

    local now = os.clock()
    local attack_element = diagnostic_value_text(
        read_struct_field(damage_result, "AttackElementType")
    )
    local weapon_type = diagnostic_value_text(
        read_struct_field(damage_result, "WeaponType")
    )
    local body_part = diagnostic_value_text(
        read_struct_field(damage_result, "BodyPartsType")
    )
    local base_power =
        tonumber(read_struct_field(damage_result, "BasePower")) or 0
    local ignore_equip = diagnostic_bool_text(
        read_struct_field(
            damage_result,
            "IgnorePlayerEquipItemDamage"
        )
    )
    local cannot_kill = diagnostic_bool_text(
        read_struct_field(damage_result, "bCannotKill")
    )

    emit_source_correlation(
        now,
        source_type,
        source_name,
        source_actor,
        resolved_source_actor_name,
        defender_name,
        actual_damage,
        base_power,
        body_part,
        weapon_type
    )

    append_line(string.format(
        "M|%.3f|%d|%s|%d|%d|%s|%s|%s|%s|%s|%s|%s|%s|%d|%s|%s\n",
        now,
        damage_metadata_sequence,
        sanitize_field(hook_name),
        actual_damage,
        raw_damage,
        sanitize_field(raw_attacker_name),
        sanitize_field(resolved_source_actor_name),
        sanitize_field(defender_name),
        sanitize_field(source_type),
        sanitize_field(source_name),
        sanitize_field(attack_element),
        sanitize_field(weapon_type),
        sanitize_field(body_part),
        base_power,
        sanitize_field(ignore_equip),
        sanitize_field(cannot_kill)
    ))
end


local function register_hook(path, label, callback)
    local ok, pre_id, post_id = pcall(function()
        return RegisterHook(path, callback)
    end)

    if ok then
        verbose_print(string.format(
            "[RogueModeTelemetry] %s registered: pre=%s post=%s\n",
            label,
            tostring(pre_id),
            tostring(post_id)
        ))
    else
        print(string.format(
            "[RogueModeTelemetry][ERROR] %s failed: %s\n",
            label,
            tostring(pre_id)
        ))
    end
end


ExecuteInGameThread(function()
    append_line(string.format(
        "V|%.3f|v0.9.0-RC12.4d|RAID_TEAM_SERVER_TRAVEL_STABILITY\n",
        os.clock()
    ))
    -- Rich damage metadata supplements the stable D records. The tracker
    -- correlates M records with accepted D records for source breakdowns.
    -- PalActionBase lifecycle gives us the action instance before damage is
    -- produced. PalActionWazaBase-derived instances expose GetWazaID(), so
    -- these records can identify skills that never pass through
    -- PalAttackFilter.
    register_hook(
        "/Script/Pal.PalActionBase:OnBeginAction",
        "Pal action begin diagnostic hook",
        function(Context)
            emit_action_lifecycle("BEGIN", Context)
        end
    )

    register_hook(
        "/Script/Pal.PalActionBase:OnEndAction",
        "Pal action end diagnostic hook",
        function(Context)
            emit_action_lifecycle("END", Context)
        end
    )

    register_hook(
        "/Script/Pal.PalActionBase:OnBreakAction",
        "Pal action break diagnostic hook",
        function(Context)
            emit_action_lifecycle("BREAK", Context)
        end
    )

    -- Shooter probes capture the actual equipped PalWeaponBase actor. Its
    -- item ID and class are more reliable than DamageResult.WeaponType,
    -- which currently labels the Mechanical Bow as internal type 3.
    register_hook(
        "/Script/Pal.PalShooterComponent:AttachWeapon",
        "Shooter attach weapon diagnostic hook",
        function(Context, Weapon, SkipLocalControlCheck)
            emit_weapon_probe(
                "ATTACH",
                Context,
                Weapon,
                SkipLocalControlCheck
            )
        end
    )

    register_hook(
        "/Script/Pal.PalShooterComponent:AttachWeapon_ForPartnerSkillPalWeapon_ToAll",
        "Partner-skill weapon attach diagnostic hook",
        function(Context, Weapon)
            emit_weapon_probe(
                "PARTNER_ATTACH",
                Context,
                Weapon,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalShooterComponent:OnChangedBullet",
        "Shooter bullet-change diagnostic hook",
        function(Context, WeaponActor, BulletItemId)
            emit_weapon_probe(
                "CHANGED_BULLET",
                Context,
                WeaponActor,
                BulletItemId
            )
        end
    )

    register_hook(
        "/Script/Pal.PalShooterComponent:PullTrigger",
        "Shooter trigger diagnostic hook",
        function(Context)
            emit_weapon_probe(
                "TRIGGER",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalShooterComponent:OnShootBullet",
        "Shooter bullet-fired diagnostic hook",
        function(Context)
            emit_weapon_probe(
                "SHOT",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalShooterComponent:BowPullAnimeEnd",
        "Bow release diagnostic hook",
        function(Context)
            emit_weapon_probe(
                "BOW_RELEASE",
                Context,
                nil,
                nil
            )
        end
    )



    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:OnActivatedAsPartner",
        "Partner skill activated-as-partner hook",
        function(Context)
            emit_partner_skill_event(
                "ACTIVATED_PARTNER",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:OnActivatedAsOtomoHolder",
        "Partner skill activated-as-holder hook",
        function(Context)
            emit_partner_skill_event(
                "ACTIVATED_HOLDER",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:CallOnStart_ToAll",
        "Partner skill start hook",
        function(Context)
            emit_partner_skill_event(
                "START",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:OnExec",
        "Partner skill execution hook",
        function(Context)
            emit_partner_skill_event(
                "EXEC",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:CallOnEffectTimeChanged_ToAll",
        "Partner skill effect-time hook",
        function(Context, EffectTime, EffectTimeMax)
            emit_partner_skill_event(
                "EFFECT_TIME",
                Context,
                EffectTime,
                EffectTimeMax
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:OnComplated",
        "Partner skill completed hook",
        function(Context)
            emit_partner_skill_event(
                "COMPLETED",
                Context,
                nil,
                nil
            )
        end
    )

    register_hook(
        "/Script/Pal.PalPartnerSkillParameterComponent:CallOnCoolDownCompleted_ToAll",
        "Partner skill cooldown-completed hook",
        function(Context)
            emit_partner_skill_event(
                "COOLDOWN_COMPLETE",
                Context,
                nil,
                nil
            )
        end
    )

    -- v0.8.5 proved the direct AddStatus / RemoveStatus functions register,
    -- but they do not execute for the replicated tower-boss status on this
    -- client. Probe the replication, delegate, and concrete PalStatusBase
    -- object layers instead.
    register_hook(
        "/Script/Pal.PalStatusComponent:OnRep_ExecutionStatusList",
        "Replicated status-list diagnostic hook",
        function(Context)
            scan_replicated_statuses(Context)
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:OnAddStatus__DelegateSignature",
        "Status added delegate diagnostic hook",
        function(Context, StatusComponent, StatusID, Status)
            record_status_object(
                "DELEGATE_ADD",
                Status,
                StatusComponent,
                StatusID,
                false,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:OnRemoveStatus__DelegateSignature",
        "Status removed delegate diagnostic hook",
        function(Context, StatusComponent, StatusID)
            remove_status_object_by_id(
                "DELEGATE_REMOVE",
                StatusComponent,
                StatusID
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusBase:OnBeginSomeStatus",
        "Status-base begin-some diagnostic hook",
        function(Context)
            emit_partner_buff_status(
                "BASE_BEGIN_SOME",
                Context,
                true
            )
            handle_status_base_event(
                "BASE_BEGIN_SOME",
                Context,
                false,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusBase:OnBeginStatus",
        "Status-base begin diagnostic hook",
        function(Context)
            emit_partner_buff_status(
                "BASE_BEGIN",
                Context,
                true
            )
            handle_status_base_event(
                "BASE_BEGIN",
                Context,
                false,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusBase:TickStatus",
        "Status-base tick diagnostic hook",
        function(Context, DeltaTime)
            emit_partner_buff_status(
                "BASE_TICK",
                Context,
                false
            )
            handle_status_base_event(
                "BASE_TICK",
                Context,
                false,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusBase:OnEndStatus",
        "Status-base end diagnostic hook",
        function(Context)
            emit_partner_buff_status(
                "BASE_END",
                Context,
                true
            )
            handle_status_base_event(
                "BASE_END",
                Context,
                true,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusBase:OnBreakStatus",
        "Status-base break diagnostic hook",
        function(Context)
            emit_partner_buff_status(
                "BASE_BREAK",
                Context,
                true
            )
            handle_status_base_event(
                "BASE_BREAK",
                Context,
                true,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatus_ToServer",
        "Status add-server diagnostic hook",
        function(Context, StatusID, Param, IssuerID)
            handle_status_event(
                Context,
                "ADD_SERVER",
                StatusID,
                Param,
                IssuerID,
                nil,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatus_ToClient",
        "Status add-client diagnostic hook",
        function(Context, StatusID, Param)
            handle_status_event(
                Context,
                "ADD_CLIENT",
                StatusID,
                Param,
                nil,
                nil,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatusParameter",
        "Status parameter diagnostic hook",
        function(Context, StatusID, Param)
            handle_status_event(
                Context,
                "ADD_PARAMETER",
                StatusID,
                Param,
                nil,
                nil,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatusInvokerParameter",
        "Status invoker-parameter diagnostic hook",
        function(Context, StatusID, Param, InvokerID)
            handle_status_event(
                Context,
                "ADD_INVOKER_PARAMETER",
                StatusID,
                Param,
                nil,
                InvokerID,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatusInvoker",
        "Status invoker diagnostic hook",
        function(Context, StatusID, InvokerID)
            handle_status_event(
                Context,
                "ADD_INVOKER",
                StatusID,
                nil,
                nil,
                InvokerID,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:AddStatus",
        "Status add diagnostic hook",
        function(Context, StatusID)
            handle_status_event(
                Context,
                "ADD_LOCAL",
                StatusID,
                nil,
                nil,
                nil,
                false
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:RemoveStatus_ToServer",
        "Status remove-server diagnostic hook",
        function(Context, StatusID, IssuerID)
            handle_status_event(
                Context,
                "REMOVE_SERVER",
                StatusID,
                nil,
                IssuerID,
                nil,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:RemoveStatus",
        "Status remove diagnostic hook",
        function(Context, StatusID)
            handle_status_event(
                Context,
                "REMOVE_LOCAL",
                StatusID,
                nil,
                nil,
                nil,
                true
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:RemoveStatusInvoker",
        "Status remove-invoker diagnostic hook",
        function(Context, InvokerID)
            handle_status_remove_invoker(
                Context,
                InvokerID
            )
        end
    )

    register_hook(
        "/Script/Pal.PalStatusComponent:RemoveAll",
        "Status remove-all diagnostic hook",
        function(Context)
            handle_status_remove_all(Context)
        end
    )

    register_hook(
        "/Script/Pal.PalCharacterParameterComponent:OnDamage",
        "Damage metadata hook",
        function(Context, DamageResult)
            emit_damage_metadata(
                DamageResult,
                "OnDamage"
            )
        end
    )

    register_hook(
        "/Script/Pal.PalCharacterParameterComponent:OnSlipDamage",
        "Slip metadata hook",
        function(Context, DamageResult)
            emit_slip_damage_diagnostic(
                Context,
                DamageResult
            )
            emit_damage_metadata(
                DamageResult,
                "OnSlipDamage"
            )
        end
    )

    register_hook(
        "/Script/Pal.PalDamageReactionComponent:CallOnActualDamageProcessed_ToAll",
        "Damage hook",
        function(Context, Attacker, Defender, ActualDamage)
            local damage = tonumber(get_value(ActualDamage)) or 0

            if damage <= 0 then
                return
            end

            local attacker_object = get_value(Attacker)
            local defender_actor = get_value(Defender)
            local defender_name = clean_name(object_name(defender_actor))
            local source_type,
                source_name,
                source_actor,
                source_actor_name,
                source_name_priority =
                get_source_metadata(attacker_object)
            local component_key = get_component_key(Context)

            emit_local_player(
                defender_actor,
                "damage",
                false
            )

            -- Promote accepted Raid Army Pal damage instead of discarding it
            -- as OTHER. This includes spawned _otomo instances such as
            -- Panthalus that do not expose a readable Pal parameter component.
            source_type,
                source_name,
                source_name_priority =
                apply_raid_pal_fallback(
                    source_type,
                    source_name,
                    source_actor,
                    source_actor_name,
                    source_name_priority,
                    defender_name
                )

            if is_pal_character(defender_actor) then
                local defender_display_name, defender_name_priority =
                    get_pal_name_info(
                        defender_actor,
                        defender_name
                    )

                emit_actor_name(
                    defender_actor,
                    defender_display_name,
                    defender_name_priority,
                    "damage defender"
                )
            end

            if source_type == "PAL" or
                source_type == "RAID_PAL" then
                emit_actor_name(
                    source_actor,
                    source_name,
                    source_name_priority,
                    "damage source"
                )
                emit_pal_owner(source_actor, "damage")
            end

            if source_type == "PAL" and active_pal_actor_name == nil then
                emit_pal_state(source_actor, true, "damage fallback")
            end

            if component_key ~= nil then
                defender_by_component[component_key] = defender_name
            end

            if source_type == "PLAYER" or
                source_type == "PAL" or
                source_type == "RAID_PAL" then
                friendly_damaged_defender_by_actor[defender_name] = true
            end

            append_line(string.format(
                "D|%.3f|%d|%s|%s|%s|%s\n",
                os.clock(),
                damage,
                sanitize_field(source_actor_name),
                sanitize_field(defender_name),
                sanitize_field(source_type),
                sanitize_field(source_name)
            ))

            verbose_print(string.format(
                "[RogueModeTelemetry][DAMAGE] %s (%s) -> %s: %d\n",
                source_name,
                source_type,
                defender_name,
                damage
            ))
        end
    )

    register_hook(
        "/Script/Pal.PalCharacter:SetActiveActor",
        "Active Pal hook",
        function(Context, Active)
            local character = get_value(Context)
            local is_active = get_value(Active) == true
            emit_pal_state(character, is_active, "PalCharacter.SetActiveActor")
        end
    )

    register_hook(
        "/Script/Pal.PalCharacter:OnRep_IsPalActiveActor",
        "Replicated active Pal hook",
        function(Context, PrevIsActiveActor)
            local character = get_value(Context)
            emit_pal_state(
                character,
                get_active_actor_flag(character),
                "PalCharacter.OnRep_IsPalActiveActor"
            )
        end
    )

    -- Primary death path: the Context is the PalCharacter that died.
    register_hook(
        "/Script/Pal.PalCharacter:OnDeadCharacter",
        "PalCharacter death hook",
        function(Context, DeadInfo)
            local dead_character = get_value(Context)
            emit_death(object_name(dead_character), "PalCharacter.OnDeadCharacter")
        end
    )

    -- Secondary death path: extract PalDeadInfo.SelfActor. The object dump
    -- shows SelfActor as the dead actor at offset 0x10 in PalDeadInfo.
    register_hook(
        "/Script/Pal.PalDamageReactionComponent:CallDeadDelegate_ToALL",
        "DamageReaction death hook",
        function(Context, DeadInfo)
            local dead_actor = get_dead_info_self_actor(DeadInfo)
            local actor_name = clean_name(object_name(dead_actor))

            if actor_name == "nil" or actor_name == "invalid" or actor_name == "unknown" then
                local component_key = get_component_key(Context)

                if component_key ~= nil then
                    actor_name = defender_by_component[component_key] or "unknown"
                end
            end

            if actor_name == "unknown" then
                actor_name = get_component_owner_name(Context)
            end

            emit_death(actor_name, "DamageReaction.CallDeadDelegate")
        end
    )

    -- Raid bosses can finish by being destroyed/despawned instead of using
    -- PalCharacter.OnDeadCharacter. These generic hooks only emit a death
    -- event for an actor previously damaged by the local player or active Pal.
    register_hook(
        "/Script/Engine.Actor:ReceiveDestroyed",
        "Actor destroyed fallback hook",
        function(Context)
            emit_tracked_actor_removal(
                Context,
                "Actor.ReceiveDestroyed"
            )
        end
    )

    register_hook(
        "/Script/Engine.Actor:ReceiveEndPlay",
        "Actor end-play fallback hook",
        function(Context, EndPlayReason)
            emit_tracked_actor_removal(
                Context,
                "Actor.ReceiveEndPlay"
            )
        end
    )

    register_hook(
        "/Script/Engine.Actor:K2_DestroyActor",
        "Actor destroy-request fallback hook",
        function(Context)
            emit_tracked_actor_removal(
                Context,
                "Actor.K2_DestroyActor"
            )
        end
    )
end)
