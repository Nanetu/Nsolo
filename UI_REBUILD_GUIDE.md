# Wiring up your rebuilt panels

**Read this once, then keep it open while you work.** Everything here happens in the Unity
Inspector. You will not open a script, and you will not drag anything into any slot.

---

## The one-sentence version

Put **one component on the screen** saying which screen it is, and **one component on each button
or label** saying what it is. That's the whole job. The code finds them.

---

## Before you start (do this once)

1. Open the project in Unity.
2. It will compile the new scripts. Wait for the spinner in the bottom-right to stop.
3. Check the Console (Window → General → Console). You want **no red errors**. Yellow is fine.

If you see red errors, stop and tell me what they say. Don't carry on.

---

## Recipe for ANY new panel

Say you've just finished building your new **Mode** screen. Here's what you do.

### Step 1 — Make the panel one object

Your screen should be **one GameObject that is a direct child of the Canvas**, with all its pieces
(background, buttons, text) **inside it**.

```
Canvas
 └── MyNewModePanel        ← this is "the panel"
      ├── Background
      ├── VsComputerCard
      ├── VsHumanCard
      ├── ContinueButton
      └── BackButton
```

### Step 2 — Tag the panel

1. Click **MyNewModePanel** in the Hierarchy.
2. In the Inspector, click **Add Component**.
3. Type `Nsolo Panel` and press Enter.
4. Find the **Id** dropdown and pick **Mode - vs Computer / vs Human**.

Leave every other setting on this component alone. The defaults give the panel its slide-in and
make its contents arrive one after another.

### Step 3 — Tag each button and label

For **every** button, and every piece of text the game needs to write into:

1. Click the object (e.g. **ContinueButton**).
2. **Add Component** → type `Nsolo Element` → Enter.
3. Open the dropdown and pick what it is (e.g. **Mode/CONTINUE**).

That's it for that button. Repeat for the rest.

### Step 4 — Turn the panel off

Untick the checkbox next to the panel's name at the top of the Inspector, so it starts hidden.
The game switches on whichever screen it needs.

### Step 5 — Leave the old panel exactly where it is

**Do not delete the old screen yet.** The moment your new one is tagged, the game uses yours and
switches the old one off by itself. You'll see a line in the Console saying so. Deleting comes
later, once everything is tested.

### Step 6 — Check your work

Menu bar → **Nsolo → Check UI Wiring**. It prints a report in the Console telling you what's
tagged, what's missing, and what's tagged twice. Run it whenever you finish a screen.

### Step 7 — Press Play and try it

---

## What you do NOT have to do any more

You may have done these before. **Stop doing them.** They are handled for you now, and doing them
by hand can cause a button to fire twice.

| Don't do this | Why not |
|---|---|
| Add a **Button** component | Added automatically if the object doesn't have one |
| Fill in **On Click ()** in the Inspector | Wired automatically, and any entry you add is cleared |
| Drag the panel into a slot on MenuManager | Found automatically by its Id |
| Drag a label into a slot on MenuManager | Found automatically by its Id |
| Add **UI Press Feedback** | Added automatically to every tagged button |
| Add **Panel Transition** | Added automatically to every tagged panel |
| Worry about the click sound | Every button in the game already gets one |
| Worry about the hitbox being the wrong size | Your button IS the artwork now, so its own rect is the hitbox |

---

## Exactly what to tag on each screen

Work down the list for whichever screen you've built. Anything marked *optional* can be skipped —
the game copes without it.

### Main menu
| Object | Pick |
|---|---|
| PLAY button | `Main menu/PLAY` |
| PLAY ONLINE button | `Main menu/PLAY ONLINE` |
| PROFILE button | `Main menu/PROFILE` |
| HOW TO PLAY button | `Main menu/HOW TO PLAY` |
| QUIT button | `Main menu/QUIT` |
| The text greeting the player *(optional)* | `Main menu/Label - player name` |

### Mode (vs Computer / vs Human)
| Object | Pick |
|---|---|
| vs COMPUTER card | `Mode/Card - vs COMPUTER` |
| vs HUMAN card | `Mode/Card - vs HUMAN` |
| CONTINUE button | `Mode/CONTINUE` |
| BACK button | `BACK (any screen...)` |
| Tick/glow on the computer card *(optional)* | `Mode/Tick on the vs COMPUTER card` |
| Tick/glow on the human card *(optional)* | `Mode/Tick on the vs HUMAN card` |

> The two ticks are the objects that show which card is selected. Build them, switch them **off**
> in the Inspector, and the game turns the right one on. Skip them and selection just won't show.

### Difficulty
| Object | Pick |
|---|---|
| EASY card | `Difficulty/Card - EASY` |
| MEDIUM card | `Difficulty/Card - MEDIUM` |
| HARD card | `Difficulty/Card - HARD` |
| START GAME button | `Difficulty/START GAME` |
| BACK button | `BACK (any screen...)` |
| The three ticks *(optional)* | `Difficulty/Tick on the ... card` |

### Profile
| Object | Pick |
|---|---|
| BACK button | `BACK (any screen...)` |
| Username text | `Profile/Label - username` |
| Games played number | `Profile/Label - games played` |
| Games won number | `Profile/Label - games won` |
| Win rate | `Profile/Label - win rate` |
| Fastest win | `Profile/Label - fastest win` |
| Easy wins | `Profile/Label - easy wins` |
| Hard wins | `Profile/Label - hard wins` |
| Breakdown text | `Profile/Label - breakdown` |
| EDIT NAME button | `Profile/EDIT NAME` |
| The avatar picture | `Profile/Avatar - the picture itself` |
| The big initial letter | `Profile/Avatar - the initial letter` |
| Tap-the-avatar button | `Profile/Avatar - tap to change` |
| Name typing box *(optional)* | `Profile/Name box (input field)` |

> The **achievement badges** and the **list of avatar pictures** are the two things still done the
> old way, in the ProfileManager slots. They're content, not wiring — which badge means what is a
> decision no dropdown can make. Leave those slots as they are.

### Online (Create / Join)
| Object | Pick |
|---|---|
| CREATE ROOM button/card | `Online/CREATE ROOM` |
| JOIN ROOM button/card | `Online/JOIN ROOM` |
| BACK button | `BACK (any screen...)` |

### Lobby
| Object | Pick |
|---|---|
| The big room code text | `Lobby/Label - room code` |
| START GAME button | `Lobby/START GAME` |
| LEAVE / BACK button | `Lobby/LEAVE (asks first)` |
| Player 1 name | `Lobby/Label - player 1 name` |
| Player 2 name | `Lobby/Label - player 2 name` |
| Player 1 status ("Host", "Ready") | `Lobby/Label - player 1 status` |
| Player 2 status | `Lobby/Label - player 2 status` |
| "Waiting for someone to join…" text *(optional)* | `Lobby/Label - waiting hint` |
| COPY CODE button *(optional)* | `Lobby/COPY CODE` |
| SHARE CODE button *(optional)* | `Lobby/SHARE CODE` |

> START GAME is **hidden**, not greyed, until an opponent joins. That's deliberate — don't build a
> disabled-looking state for it.

### Pause
| Object | Pick |
|---|---|
| RESUME button | `Pause/RESUME` |
| RESTART button | `Pause/RESTART` |
| MAIN MENU button | `Pause/MAIN MENU` |
| HOW TO PLAY button *(optional)* | `Pause/HOW TO PLAY` |
| Music slider | `Pause/Slider - music volume` |
| Sound slider | `Pause/Slider - sound volume` |
| Music "[70%]" text *(optional)* | `Pause/Label - music percent` |
| Sound "[85%]" text *(optional)* | `Pause/Label - sound percent` |
| Vibration toggle *(optional)* | `Pause/Toggle - vibration` |
| Tips toggle *(optional)* | `Pause/Toggle - in-game tips` |

> The sliders must be real Unity **Slider** components and the toggles real **Toggle** components.
> Those two are the exception to "it adds what's missing" — a slider is a thing you build, not a
> thing that can be guessed.

### Game over
| Object | Pick |
|---|---|
| VICTORY / DEFEAT title | `Game over/Label - VICTORY or DEFEAT` |
| PLAY AGAIN button | `Game over/PLAY AGAIN` |
| MAIN MENU button | `Game over/MAIN MENU` |
| Your stone count | `Game over/Label - your stones` |
| Opponent stone count | `Game over/Label - opponent stones` |
| Difficulty text | `Game over/Label - difficulty` |
| Time taken | `Game over/Label - time taken` |
| Total wins | `Game over/Label - total wins` |
| Score summary line | `Game over/Label - score summary` |

### Tutorial / How to play
| Object | Pick |
|---|---|
| CLOSE / BACK button | `Tutorial/CLOSE` |
| START PLAYING button | `Tutorial/START PLAYING` |

### The board screen (HUD)
Only if you rebuild the in-game buttons.

| Object | Pick |
|---|---|
| PAUSE button | `In game/PAUSE` |
| UNDO button | `In game/UNDO` |
| The bottom pill (START / HINT) | `In game/Main action button` |

> The bottom pill is **one** button that changes what it says. Use `Main action button`, not
> `HINT` — the game decides which of the two it currently is.

---

## About BACK

Every BACK button in the game gets the **same** entry: `BACK (any screen...)`.

You don't pick a destination. It works out where back is from where you are — out of Difficulty
goes to Mode, out of the tutorial goes back to the pause screen if that's where you opened it, and
out of the lobby asks you first because leaving closes the room.

The **Lobby** and **Online** screens are the one exception worth knowing: their BACK can use
`Lobby/LEAVE (asks first)` instead. Both do the same thing there, so either is correct.

---

## Troubleshooting

| What you see | What it means |
|---|---|
| Console: *"nothing to bind X to"* | The controller isn't in the scene. Tell me — it's not something you can fix in the Inspector. |
| Console: *"two panels are both tagged Mode"* | You tagged the old one as well. Set the old one's Id back to `None`. |
| Console: *"X is on more than one object"* | Two labels claim the same job. Only one can win — clear the other. |
| Button does nothing, no error | You tagged the object but its Id is still `None`. Run **Nsolo → Check UI Wiring**. |
| Button fires twice / skips two screens | There's a leftover **On Click ()** entry AND a tag. Delete the On Click () entry. (Shouldn't happen — the default clears them — unless you unticked *Replace Existing Clicks*.) |
| Panel is there but invisible | It's probably still switched off. Or another panel is on top of it. |
| Everything fades out and never comes back | A screen is tagged with an Id that nothing ever shows. Check the Id is the one you meant. |
| Tapping does nothing anywhere on a screen | An old full-screen panel is still switched on over the top. Switch it off. |

---

## The three settings on Nsolo Element you can ignore

They're there for odd cases. Defaults are right almost always.

- **Replace Existing Clicks** — on. Drops any On Click () you set by hand. Leave it on.
- **Add Press Feedback** — on. The squash-and-glow when you touch it. Turn off only for something
  that shouldn't react.
- **Press Shape** — Auto. Long thin buttons get round ends, square ones get soft corners.

## The four settings on Nsolo Panel you can ignore

- **Add Transition** — on. The screen slides and fades instead of cutting.
- **Stagger Contents** — on. Pieces arrive one after another, ~35ms apart. This is the thing that
  makes a screen feel alive rather than pasted on. If a screen looks busy or jittery, turn this
  one off and see if you prefer it.
- **Stagger Seconds / Element Seconds / Element Lift** — the timing and the small rise. Only worth
  touching if you want to tune the feel.

---

## When you're done with all the screens

1. Run **Nsolo → Check UI Wiring** one last time. Aim for "No problems found."
2. Press Play and walk the whole game: menu → mode → difficulty → play → pause → resume → quit to
   menu → profile → tutorial → online → lobby.
3. **Then** delete the old panels. Not before.
4. Save the scene.

Leave the deleting until we're next working together if you'd rather — that and clearing out the
now-unused code is exactly what the next session is for.
