# PitStoneVisualizer

This file contains the `PitStoneVisualizer` Unity component responsible for rendering and animating the stones on the Nsolo board during runtime.

## Purpose

`PitStoneVisualizer` is a runtime-only visualizer that:
- spawns stone prefabs into pit and store containers based on the authoritative `GameBoard` state
- clears and rebuilds stone visuals when the board is refreshed
- animates sowing moves using stone pickup, carry, and drop motion
- tracks spawned stone GameObjects so they can be cleaned up and reused logically

## Key concepts

- `holes`: array of 48 optional `GameObject` transforms for board pits.
- `storeP1`, `storeP2`: optional `Transform` references for each player's captured stone store.
- `stonePrefab`: prefab used to instantiate visual stones.
- `spawnedStoneRoot`: optional container transform for generated stone GameObjects.
- `stonesByPit`, `storeP1Stones`, `storeP2Stones`: runtime lists that track which spawned stones belong to each pit or store.

## Public API

- `SetHoles(GameObject[] sceneHoles)`: assign scene hole objects from outside.
- `Refresh(GameBoard board, int capturedP1, int capturedP2)`: clear existing visuals and rebuild all stone visuals from the given board state.
- `PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)`: plays sowing animations for a move result, including the initial refresh and final board refresh.

## Main runtime flow

1. `Awake()`
   - ensures `spawnedStoneRoot` exists
   - initializes internal stone lists
   - configures an audio source and stone hit clip

2. `Refresh(...)`
   - validates input and logs board state
   - clears previous spawned stones
   - iterates over all pits and spawns the appropriate number of stone visuals
   - spawns captured stones into each store if configured

3. `SpawnStoneGroup(...)`
   - calculates a visual count limited by a maximum
   - decides a spawn position for each stone based on pit/store placement rules
   - instantiates the prefab parented to the hole/store transform
   - applies explicit local scale adjustment
   - records stones into the global `spawnedStones` list and the specific pit/store list

4. `PlayMoveAnimation(...)`
   - refreshes the start board state
   - plays each `SowingSegment` animation
   - refreshes the final board state

5. `AnimateSowingSegment(...)`
   - picks up stones from the source pit
   - lifts them and staggers motion
   - moves stone objects to landing pits one-by-one
   - updates the destination pit stone list
   - plays hit audio when stones land

## Important helper groups

### Stone placement and layout

- `GetPitTransform(int row, int col)`
- `GetPitPosition(int row, int col)`
- `GetPitStonePosition(int row, int col, int visualIndex)`
- `GetStoneGroupPosition(...)`
- `GetCenteredPileOffset(...)`
- `GetOrganicStoreStonePosition(...)`
- `GetOrganicClusterOffset(...)`
- `GetScatterOffset(...)`

These methods compute local stone positions for pits and stores and support optional pile or scatter layouts.

### Animation helpers

- `PickUpStones(int row, int col)`
- `MoveStone(Transform stone, Vector3 start, Vector3 end, float duration, float arcHeight)`
- `GetCarryOffset(int index, int total)`
- `PlayStoneHit()`
- `CreateStoneHitClip()`

### Cleanup helpers

- `ClearStones()`
- `ClearStonesImmediate()`
- `ClearStoneLists()`

## Refactor candidates

This class is relatively large and has multiple responsibilities. Good split points include:

1. `StoneSpawner` / `StonePlacement` service
   - move `SpawnStoneGroup(...)` and the position-generation methods into a separate class.
   - keep `Refresh(...)` and `ClearStones(...)` in the visualizer, while delegating spawn layout.

2. `MoveAnimationController`
   - move `PlayMoveAnimation(...)`, `AnimateSowingSegment(...)`, `MoveStone(...)`, `PickUpStones(...)`, and `GetCarryOffset(...)` into a dedicated animation class.
   - this would keep the visualizer focused on state refresh and let the animation class handle motion.

3. `AudioFeedback` or `StoneSoundPlayer`
   - extract `PlayStoneHit()` and `CreateStoneHitClip()` if you want independent audio logic.

4. `StoneTracking` / data wrapper
   - encapsulate `stonesByPit`, `storeP1Stones`, `storeP2Stones`, and `spawnedStones` into a small runtime tracker object or struct.

## Where to cut first

- `MoveStone(...)`, `AnimateSowingSegment(...)`, and `PlayMoveAnimation(...)` form a natural animation block. Extracting exactly that block will make the file much shorter and keep one class responsible for board refresh and one for animation logic.
- `SpawnStoneGroup(...)` plus all position helpers are another obvious chunk. If you want to separate visual layout from board-sync logic, those methods can move into a `PitStoneLayout` helper.

## Summary

`PitStoneVisualizer` currently does three jobs:
- visual state reconstruction from the board
- stone placement and layout
- move animation and audio feedback

If you want a lighter file, split it into:
- `PitStoneVisualizer` (board-to-visual state mapping)
- `PitStoneAnimator` (sowing and movement animation)
- `StoneLayoutProvider` (spawn positions and scatter/pile rules)
- optional `StoneAudio` if you want audio separate
