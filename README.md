# OBS to ABB
based off pull to obs :
https://github.com/Miu-B/PullToOBS:
## Features

* **Automatic Recording** -- the whole point
  * Starts OBS recording when combat begins (detected via Dalamud)
  * Stops recording after a 5-second grace period when combat ends
  * If you re-enter combat during that grace period, the pending stop is cancelled and you get one continuous recording instead of two fragments

* **Replay Buffer Integration** -- never miss the prepull
  * Automatically starts the OBS replay buffer when the plugin connects
  * Saves the replay buffer 5 seconds into the encounter, capturing everything that happened before the pull
  * You'll end up with two files per encounter: a replay buffer clip (prepull) and a full recording

* **Visual Status Indicator** -- know what OBS is doing at a glance
  * Always-visible on-screen dot showing the current OBS state
  * **Red pulsing dot**: Recording in progress
  * **Orange dot**: Replay buffer active (connected, not recording)
  * **Green dot**: Connected to OBS
  * **Grey dot**: Not connected
  * Draggable when the config window is open
  * Adjustable scale (0.5x - 2.0x)

* **Simple Configuration**
  * OBS WebSocket URL and password
  * Optional auto-connect on plugin start
  * Indicator position, scale, and visibility settings
  * All settings are saved automatically

## Companion Tool

my changes: built in stiching if ffmpeg is installed, and file renaming with pull info, boss hp%, info from aetherblackbox ipc, 

## Requirements

* [OBS Studio](https://obsproject.com/) with **WebSocket v5 enabled** (OBS > Tools > WebSocket Server Settings)
* **Replay Buffer enabled in OBS** (OBS > Settings > Output > Replay Buffer)

> **The Replay Buffer must be enabled in OBS before connecting.** This is what captures the prepull clip.
> You can confirm it's active by the indicator turning **orange** after connecting. If the indicator stays
> **green** instead of orange, go into OBS Settings > Output > Replay Buffer and enable it, then reconnect.

## Installation

PullToOBS is available in the official Dalamud plugin repository.

Open the Plugin Installer in-game (`/xlplugins`), search for **PullToOBS**, and install it.

## How To Use

### Getting Started

1. Enable OBS WebSocket v5 (OBS > Tools > WebSocket Server Settings)
2. Set up a Replay Buffer in OBS (Settings > Output > Replay Buffer) -- this is what captures the prepull
3. Open PullToOBS config with `/pulltoobs` or `/pto`
4. Enter your OBS WebSocket URL and password, then click Connect
5. The indicator shows up on screen -- you're good to go
6. Enter combat and recording starts automatically

### Commands

* `/pulltoobs` or `/pto` - Toggle the configuration window
* `/pulltoobs obs` or `/pto obs` - Toggle OBS connection
* `/pulltoobs rec` or `/pto rec` - Toggle standby mode (suppresses automatic recording)
* `/pulltoobs show` or `/pto show` - Show the indicator
* `/pulltoobs hide` or `/pto hide` - Hide the indicator

### Adjusting Indicator

1. Open the configuration window with `/pulltoobs`
2. While the configuration window is open, drag the indicator to your desired position
3. Use the "Indicator Scale" slider to adjust size
4. Position is saved automatically when you finish dragging

## Configuration

All settings are saved automatically, so you can just set things up once and forget about it (forgetting is what we're good at, after all):

* **WebSocket URL** - OBS WebSocket server address (default: `ws://localhost:4455`)
* **Password** - OBS WebSocket server password
* **Auto-connect on start** - Automatically connect to OBS when the plugin loads
* **Indicator Scale** - Scale multiplier for the indicator (0.5x to 2.0x)
* **Hide Indicator** - Toggle indicator visibility

## Development


## License

AGPL-3.0-or-later

## Credits

Based on [SamplePlugin](https://github.com/goatcorp/SamplePlugin) template by goatcorp
