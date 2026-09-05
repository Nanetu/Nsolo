#!/usr/bin/env python3
"""
Fix Codex's incorrect UI animation placement.
Moves UIPanelTransition + AnimatedUIButton from WelcomePanel to Main menu panel.
"""

import shutil
import sys

SCENE_PATH = "Assets/Prefabs/Scenes/SampleScene.unity"
BACKUP_PATH = "Assets/Prefabs/Scenes/SampleScene.unity.backup"

# GUIDs from meta files
GUID_ANIMATED_UI_BUTTON = "c6adcd746cfa45a0aa2161b99a58e914"
GUID_UI_PANEL_TRANSITION = "1a4693a1bfd04b8e9e29890b293d2101"

# New fileIDs for components we're adding to Main menu and its buttons
FID_PANEL_TRANSITION = 74009200
FID_CANVAS_GROUP = 74009201
FID_ANIM_BTN_PLAY = 74009210
FID_ANIM_BTN_TUTORIAL = 74009211
FID_ANIM_BTN_PROFILE = 74009212
FID_ANIM_BTN_QUIT = 74009213

# GameObject fileIDs
GID_WELCOME_PANEL = 2113358933
GID_MAIN_MENU_PANEL = 1065673654
GID_MAIN_MENU_PLAY = 2736840
GID_MAIN_MENU_TUTORIAL = 868118057
GID_MAIN_MENU_PROFILE = 1438269987
GID_MAIN_MENU_QUIT = 1935644899

# Component fileIDs to REMOVE from WelcomePanel and its buttons
REMOVE_FROM_WELCOME = [74009000, 74009001]  # CanvasGroup, UIPanelTransition
REMOVE_FROM_WELCOME_BUTTONS = [74008000, 74008001, 74008002, 74008003]
REMOVE_FROM_MAIN_MENU = [74009100, 74009101]  # broken refs


def main():
    shutil.copy2(SCENE_PATH, BACKUP_PATH)
    print(f"Backup created: {BACKUP_PATH}")

    with open(SCENE_PATH, "r", encoding="utf-8") as f:
        lines = f.readlines()

    # ------------------------------------------------------------------
    # 1. Remove component references from GameObjects
    # ------------------------------------------------------------------
    lines = remove_component_refs(lines, GID_WELCOME_PANEL, REMOVE_FROM_WELCOME)
    lines = remove_component_refs(lines, GID_MAIN_MENU_PANEL, REMOVE_FROM_MAIN_MENU)

    # Remove AnimatedUIButton refs from WelcomePanel buttons
    welcome_btn_gids = [1260877674, 577277040, 563933898, 2058435777]
    for gid, fid in zip(welcome_btn_gids, REMOVE_FROM_WELCOME_BUTTONS):
        lines = remove_component_refs(lines, gid, [fid])

    # ------------------------------------------------------------------
    # 2. Remove orphaned YAML blocks
    # ------------------------------------------------------------------
    for fid in REMOVE_FROM_WELCOME + REMOVE_FROM_WELCOME_BUTTONS:
        lines = remove_yaml_block(lines, fid)

    # ------------------------------------------------------------------
    # 3. Add new component refs to Main menu panel and its buttons
    # ------------------------------------------------------------------
    lines = add_component_ref(lines, GID_MAIN_MENU_PANEL, FID_PANEL_TRANSITION)
    lines = add_component_ref(lines, GID_MAIN_MENU_PANEL, FID_CANVAS_GROUP)

    lines = add_component_ref(lines, GID_MAIN_MENU_PLAY, FID_ANIM_BTN_PLAY)
    lines = add_component_ref(lines, GID_MAIN_MENU_TUTORIAL, FID_ANIM_BTN_TUTORIAL)
    lines = add_component_ref(lines, GID_MAIN_MENU_PROFILE, FID_ANIM_BTN_PROFILE)
    lines = add_component_ref(lines, GID_MAIN_MENU_QUIT, FID_ANIM_BTN_QUIT)

    # ------------------------------------------------------------------
    # 4. Set Main menu m_IsActive to 1
    # ------------------------------------------------------------------
    lines = set_is_active(lines, GID_MAIN_MENU_PANEL, 1)

    # ------------------------------------------------------------------
    # 5. Append new YAML blocks at end of file
    # ------------------------------------------------------------------
    new_blocks = build_new_yaml_blocks()
    # Ensure last line has a newline, then append
    if lines and not lines[-1].endswith("\n"):
        lines[-1] += "\n"
    lines.extend(new_blocks)

    # ------------------------------------------------------------------
    # 6. Write back
    # ------------------------------------------------------------------
    with open(SCENE_PATH, "w", encoding="utf-8") as f:
        f.writelines(lines)

    print("Scene modifications complete.")
    print(f"  - Removed UIPanelTransition + CanvasGroup from WelcomePanel")
    print(f"  - Removed AnimatedUIButton from WelcomePanel buttons")
    print(f"  - Added UIPanelTransition + CanvasGroup to Main menu panel")
    print(f"  - Added AnimatedUIButton to Main menu buttons (Play, Tutorial, Profile, Quit)")
    print(f"  - Set Main menu m_IsActive = 1")
    print(f"  - Cleaned broken refs 74009100/74009101 from Main menu")


def remove_component_refs(lines, gameobject_fid, component_fids_to_remove):
    """Remove specific component references from a GameObject's m_Component list."""
    result = []
    inside_target_go = False
    inside_components = False
    i = 0
    while i < len(lines):
        line = lines[i]

        # Detect start of target GameObject
        if f"!u!1 &{gameobject_fid}" in line:
            inside_target_go = True

        if inside_target_go:
            if line.strip() == "m_Component:":
                inside_components = True
            elif inside_components and not line.startswith("  -"):
                inside_components = False
                inside_target_go = False

            if inside_components:
                # Check if this line references one of the components to remove
                skip = False
                for fid in component_fids_to_remove:
                    if f"fileID: {fid}" in line:
                        skip = True
                        break
                if skip:
                    i += 1
                    continue

        result.append(line)
        i += 1

    return result


def remove_yaml_block(lines, fid):
    """Remove a YAML block starting with '!u!... &{fid}'."""
    result = []
    inside_block = False
    for line in lines:
        if f"&{fid}" in line and line.startswith("--- !u!"):
            inside_block = True
            continue
        if inside_block:
            if line.startswith("--- !u!"):
                inside_block = False
                result.append(line)
            continue
        result.append(line)
    return result


def add_component_ref(lines, gameobject_fid, component_fid):
    """Add a component reference to a GameObject's m_Component list."""
    result = []
    inside_target_go = False
    inside_components = False
    inserted = False
    for line in lines:
        if f"!u!1 &{gameobject_fid}" in line:
            inside_target_go = True

        if inside_target_go:
            if line.strip() == "m_Component:":
                inside_components = True
            elif inside_components and not line.startswith("  -"):
                if not inserted:
                    result.append(f"  - component: {{fileID: {component_fid}}}\n")
                    inserted = True
                inside_components = False
                inside_target_go = False

        result.append(line)

    return result


def set_is_active(lines, gameobject_fid, value):
    """Set m_IsActive for a specific GameObject."""
    result = []
    inside_target_go = False
    for line in lines:
        if f"!u!1 &{gameobject_fid}" in line:
            inside_target_go = True

        if inside_target_go and line.strip().startswith("m_IsActive:"):
            line = f"  m_IsActive: {value}\n"
            inside_target_go = False

        result.append(line)
    return result


def build_new_yaml_blocks():
    """Build the YAML blocks for new components being added."""
    blocks = []

    # CanvasGroup for Main menu panel
    blocks.append(f"--- !u!225 &{FID_CANVAS_GROUP}\n")
    blocks.append("CanvasGroup:\n")
    blocks.append("  m_ObjectHideFlags: 0\n")
    blocks.append("  m_CorrespondingSourceObject: {fileID: 0}\n")
    blocks.append("  m_PrefabInstance: {fileID: 0}\n")
    blocks.append("  m_PrefabAsset: {fileID: 0}\n")
    blocks.append(f"  m_GameObject: {{fileID: {GID_MAIN_MENU_PANEL}}}\n")
    blocks.append("  m_Enabled: 1\n")
    blocks.append("  m_Alpha: 0\n")
    blocks.append("  m_Interactable: 1\n")
    blocks.append("  m_BlocksRaycasts: 1\n")
    blocks.append("  m_IgnoreParentGroups: 0\n")
    blocks.append("\n")

    # UIPanelTransition for Main menu panel
    blocks.append(f"--- !u!114 &{FID_PANEL_TRANSITION}\n")
    blocks.append("MonoBehaviour:\n")
    blocks.append("  m_ObjectHideFlags: 0\n")
    blocks.append("  m_CorrespondingSourceObject: {fileID: 0}\n")
    blocks.append("  m_PrefabInstance: {fileID: 0}\n")
    blocks.append("  m_PrefabAsset: {fileID: 0}\n")
    blocks.append(f"  m_GameObject: {{fileID: {GID_MAIN_MENU_PANEL}}}\n")
    blocks.append("  m_Enabled: 1\n")
    blocks.append("  m_EditorHideFlags: 0\n")
    blocks.append(f"  m_Script: {{fileID: 11500000, guid: {GUID_UI_PANEL_TRANSITION}, type: 3}}\n")
    blocks.append("  m_Name:\n")
    blocks.append("  m_EditorClassIdentifier:\n")
    blocks.append("  openStartScale: 0.96\n")
    blocks.append("  openDuration: 0.22\n")
    blocks.append("  openCurve:\n")
    blocks.append("    serializedVersion: 2\n")
    blocks.append("    m_Curve: []\n")
    blocks.append("    m_PreInfinity: 2\n")
    blocks.append("    m_PostInfinity: 2\n")
    blocks.append("    m_RotationOrder: 4\n")
    blocks.append("  closeEndScale: 0.97\n")
    blocks.append("  closeDuration: 0.18\n")
    blocks.append("  closeCurve:\n")
    blocks.append("    serializedVersion: 2\n")
    blocks.append("    m_Curve: []\n")
    blocks.append("    m_PreInfinity: 2\n")
    blocks.append("    m_PostInfinity: 2\n")
    blocks.append("    m_RotationOrder: 4\n")
    blocks.append("  deactivateWhenClosed: 1\n")
    blocks.append("\n")

    # AnimatedUIButton blocks for Main menu buttons
    button_mappings = [
        (FID_ANIM_BTN_PLAY, GID_MAIN_MENU_PLAY),
        (FID_ANIM_BTN_TUTORIAL, GID_MAIN_MENU_TUTORIAL),
        (FID_ANIM_BTN_PROFILE, GID_MAIN_MENU_PROFILE),
        (FID_ANIM_BTN_QUIT, GID_MAIN_MENU_QUIT),
    ]

    for comp_fid, go_fid in button_mappings:
        blocks.append(f"--- !u!114 &{comp_fid}\n")
        blocks.append("MonoBehaviour:\n")
        blocks.append("  m_ObjectHideFlags: 0\n")
        blocks.append("  m_CorrespondingSourceObject: {fileID: 0}\n")
        blocks.append("  m_PrefabInstance: {fileID: 0}\n")
        blocks.append("  m_PrefabAsset: {fileID: 0}\n")
        blocks.append(f"  m_GameObject: {{fileID: {go_fid}}}\n")
        blocks.append("  m_Enabled: 1\n")
        blocks.append("  m_EditorHideFlags: 0\n")
        blocks.append(f"  m_Script: {{fileID: 11500000, guid: {GUID_ANIMATED_UI_BUTTON}, type: 3}}\n")
        blocks.append("  m_Name:\n")
        blocks.append("  m_EditorClassIdentifier:\n")
        blocks.append("  pressedScale: 0.94\n")
        blocks.append("  releaseOvershoot: 1.05\n")
        blocks.append("  pressDuration: 0.08\n")
        blocks.append("  releaseDuration: 0.08\n")
        blocks.append("  settleDuration: 0.1\n")
        blocks.append("  animationCurve:\n")
        blocks.append("    serializedVersion: 2\n")
        blocks.append("    m_Curve: []\n")
        blocks.append("    m_PreInfinity: 2\n")
        blocks.append("    m_PostInfinity: 2\n")
        blocks.append("    m_RotationOrder: 4\n")
        blocks.append("  clickClip: {fileID: 0}\n")
        blocks.append("  clickAudioSource: {fileID: 0}\n")
        blocks.append("\n")

    return blocks


if __name__ == "__main__":
    main()
