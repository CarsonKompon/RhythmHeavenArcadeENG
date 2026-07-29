If you plan on contributing, this file contains everything I use to make this project, and how I use it.

# Decisions Made

These are decisions I've made for this translation project, but am to further discussion:

- Remixes use their descriptions from the GBA game instead of their generic arcade descriptions
- Marching Orders is sometimes called "Marchers" in instances where the text replacement doesn't fit
- Polyrhythm keeps the name of "Polyrhythm" even though it's similar to "Built to Scale" from future titles
- "Rap Men" and "Rap Women" are preferred over "RAPMEN" and "RAPWOMEN"
- "Toss Team" is preferred over "Toss Boys"
- "Turbo Tap Trial" is preferred over "Tap Trial 2"
- "Ninja Descendant" is preferred over "Ninja Bodyguard 2"

# Software

Here's all the software I use and the quirks to them:

- Paint.NET
    - When it comes to creating text, I will typically create the text at 37px and then scale that down with the transform tools (set to Bicubic). This makes it look a bit sharper and gives you more pixel information to work with if you need to squish it on the X (which sometimes has to be done to make english text fit where the japanese text was since they use a lot less characters)
    - I would love to move most text-related stuff to photoshop at some point so the text can become editable in the future without remaking all the FX on the text but I will get to that in the future.
- Flycast
    - The `RHYTHM-TENGOKU_JAPAN` folder from this repo goes into the `data/textures/` folder of Flycast
    - Go to Settings -> Video and enable "Load Custom Textures" and "Preload Custom Textures"
        - If you are planning on contributing to this repo by making any texture edits, you should DISABLE "Preload Custom Textures" so Flycast will load any new textures (or changes made to existing textures) as soon as you load a save state, even if you made those changes while Flycast was open with the game running.
- TexManagementTool
    - Included in `tools/TexManagementTool/`, it's a .NET application with a basic GUI which lets you specify texture directories (of the original game textures from a dump, and an output directory) with options to group/organize textures without messing with the folder structure so the output directory is Flycast-ready
    - TexManagementTool also has a "Copy Image" feature, where you can also set the output brightness of the image. This is useful because the ROM has multiple copies of the same texture sheet at varying brightnesses (to fade them in/out), so you only ever have to make one master file where the others automatically get created at the set brightness
- M4Text
    - Included in `tools/M4Text.Editor/`, it's another .NET application with a basic GUI which lets you specify a working folder + folder with your own personally supplied ROM files, and then scans for ASCII/UTF-8 strings. You can then specify a string replacement to patch into the file when pressing "Export ROM"
    - You can save/load the `changes.m4text.json` file in the repo to commit your changes made to text.

# Fonts

Here's a list of all fonts used and where they are used:

- Arial
    - Used bold with no anti-aliasing at 8-10px to replace smaller pixel fonts (like "Press START to skip")
- Arial Rounded MT Bold
    - Used for the Stage Name/Descriptions on the "Select a Stage" menu. When used in this context it's also given an additional 1px outline at size 37px font and then scaled down (typically squished around 30% horizontally as well)
    - Uaws doe the Game Name/Descriptions on the "Select a Game" menu. Used in the same way as Stages.
- FOT-Kurokane Std (Megamix Modified)
    - Used for game titles in the "Select a Game" menu
    - Used for the "No Continues" text on leaderboard entries
- FOT-RodinNTLG Pro DB
    - Used for most plain text, like game descriptions, tutorial popups, ect
- FOT-Slump Std DB
    - Used for header labels like "Select a Mode", or "Select a Stage"
- FOT-Yuruka Std UB
    - Used for "Clear with a SUPERB for 1 extra play" banner
- Tim Sale Lower-Bold
    - Used for the "ENCORE", "EXPERT", "MASTER", and "EXTRA" titles on the "Select a Stage" menu
- VDL-GigaJr B
    - Used for titles on the leaderboard menu
- WarioWareInc V2 Medium
    - Used for some pixel font replacements in Rhythm Tweezers