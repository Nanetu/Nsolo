import re
from pathlib import Path

scene = Path(r"c:\Users\dell\Nsolo\Assets\Prefabs\Scenes\SampleScene.unity")
text = scene.read_text(encoding="utf-8")

text = text.replace(
    """  m_Component:
  - component: {fileID: 2113358936}
  - component: {fileID: 2113358935}
  - component: {fileID: 2113358934}
  - component: {fileID: 74009000}
  - component: {fileID: 74009001}
  m_Layer: 5
  m_Name: WelcomePanel""",
    """  m_Component:
  - component: {fileID: 2113358936}
  - component: {fileID: 2113358935}
  - component: {fileID: 2113358934}
  m_Layer: 5
  m_Name: WelcomePanel""",
)

welcome_button_refs = [
    (1260877674, 74008000),
    (577277040, 74008001),
    (563933898, 74008002),
    (2058435777, 74008003),
]
for go_id, comp_id in welcome_button_refs:
    pattern = (
        rf"(--- !u!1 &{go_id}\nGameObject:.*?m_Component:\n"
        rf"(?:  - component: \{{fileID: \d+\}}\n)+)"
        rf"  - component: \{{fileID: {comp_id}\}}\n"
    )
    text, n = re.subn(pattern, r"\1", text, count=1, flags=re.DOTALL)
    if n != 1:
        print(f"WARN: welcome button {go_id} comp removal matched {n}")

for comp_id in [74009000, 74009001, 74008000, 74008001, 74008002, 74008003]:
    text, n = re.subn(rf"--- !u!\d+ &{comp_id}\n.*?(?=\n--- !u!)", "\n", text, count=1, flags=re.DOTALL)
    if n != 1:
        print(f"WARN: delete block {comp_id} matched {n}")

text = text.replace(
    """  m_Component:
  - component: {fileID: 1065673655}
  - component: {fileID: 1065673658}
  - component: {fileID: 1065673657}
  - component: {fileID: 1065673656}
  m_Layer: 5
  m_Name: Main menu""",
    """  m_Component:
  - component: {fileID: 1065673655}
  - component: {fileID: 1065673658}
  - component: {fileID: 1065673657}
  - component: {fileID: 1065673656}
  - component: {fileID: 74009100}
  - component: {fileID: 74009101}
  m_Layer: 5
  m_Name: Main menu""",
)

text = text.replace(
    """  m_GameObject: {fileID: 1065673654}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: e6b2d90c418f47a3b5c0e7d219f8a34c, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  seconds: 0.17
  rise: 24
--- !u!114 &1065673657""",
    """  m_GameObject: {fileID: 1065673654}
  m_Enabled: 0
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: e6b2d90c418f47a3b5c0e7d219f8a34c, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  seconds: 0.17
  rise: 24
--- !u!114 &1065673657""",
)

main_buttons = [
    (2736840, 74009102),
    (868118057, 74009103),
    (1438269987, 74009104),
    (1935644899, 74009105),
]
for go_id, comp_id in main_buttons:
    pattern = (
        rf"(--- !u!1 &{go_id}\nGameObject:.*?m_Component:\n"
        rf"(?:  - component: \{{fileID: \d+\}}\n)+)"
        rf"(  m_Layer: 5\n)"
    )
    repl = rf"\1  - component: {{fileID: {comp_id}}}\n\2"
    text, n = re.subn(pattern, repl, text, count=1, flags=re.DOTALL)
    if n != 1:
        print(f"WARN: main menu button {go_id} comp add matched {n}")

text = text.replace(
    "  welcomePanel: {fileID: 2113358933}\n  tutorialPanel:",
    "  welcomePanel: {fileID: 2113358933}\n  mainMenuPanel: {fileID: 1065673654}\n  tutorialPanel:",
)

new_blocks = """--- !u!225 &74009100
CanvasGroup:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 1065673654}
  m_Enabled: 1
  m_Alpha: 1
  m_Interactable: 1
  m_BlocksRaycasts: 1
  m_IgnoreParentGroups: 0
--- !u!114 &74009101
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 1065673654}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: 1a4693a1bfd04b8e9e29890b293d2101, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  openStartScale: 0.96
  openDuration: 0.22
  openCurve:
    serializedVersion: 2
    m_Curve: []
    m_PreInfinity: 2
    m_PostInfinity: 2
    m_RotationOrder: 4
  closeEndScale: 0.97
  closeDuration: 0.18
  closeCurve:
    serializedVersion: 2
    m_Curve: []
    m_PreInfinity: 2
    m_PostInfinity: 2
    m_RotationOrder: 4
  deactivateWhenClosed: 1
--- !u!114 &74009102
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 2736840}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: c6adcd746cfa45a0aa2161b99a58e914, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  pressedScale: 0.94
  releaseOvershoot: 1.05
  pressDuration: 0.08
  releaseDuration: 0.08
  settleDuration: 0.1
  animationCurve: {serializedVersion: 2, m_Curve: [], m_PreInfinity: 2, m_PostInfinity: 2, m_RotationOrder: 4}
  clickClip: {fileID: 0}
  clickAudioSource: {fileID: 0}
--- !u!114 &74009103
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 868118057}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: c6adcd746cfa45a0aa2161b99a58e914, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  pressedScale: 0.94
  releaseOvershoot: 1.05
  pressDuration: 0.08
  releaseDuration: 0.08
  settleDuration: 0.1
  animationCurve: {serializedVersion: 2, m_Curve: [], m_PreInfinity: 2, m_PostInfinity: 2, m_RotationOrder: 4}
  clickClip: {fileID: 0}
  clickAudioSource: {fileID: 0}
--- !u!114 &74009104
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 1438269987}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: c6adcd746cfa45a0aa2161b99a58e914, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  pressedScale: 0.94
  releaseOvershoot: 1.05
  pressDuration: 0.08
  releaseDuration: 0.08
  settleDuration: 0.1
  animationCurve: {serializedVersion: 2, m_Curve: [], m_PreInfinity: 2, m_PostInfinity: 2, m_RotationOrder: 4}
  clickClip: {fileID: 0}
  clickAudioSource: {fileID: 0}
--- !u!114 &74009105
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 1935644899}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: c6adcd746cfa45a0aa2161b99a58e914, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  pressedScale: 0.94
  releaseOvershoot: 1.05
  pressDuration: 0.08
  releaseDuration: 0.08
  settleDuration: 0.1
  animationCurve: {serializedVersion: 2, m_Curve: [], m_PreInfinity: 2, m_PostInfinity: 2, m_RotationOrder: 4}
  clickClip: {fileID: 0}
  clickAudioSource: {fileID: 0}
"""

anchor = "--- !u!1 &74010001"
if anchor not in text:
    raise SystemExit("anchor not found")
if "&74009100" in text:
    raise SystemExit("blocks already inserted")
text = text.replace(anchor, new_blocks + anchor)

scene.write_text(text, encoding="utf-8")
print("Scene updated successfully")
