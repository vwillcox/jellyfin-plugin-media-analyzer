# Jellyfin Media Analyzer

## This is a fork of [ConfusedPolarBear Intro Skipper](https://github.com/ConfusedPolarBear/intro-skipper)!

Preparation as Jellyfin Core plugin
- [x] Remove frontend/backend playback injection
- [x] Remove plugin settings section for playback
- [X] Update implementation to use Jellyfin Media Segment API
- [x] Fix configPage.html updateTimestamps (update http endpoint required). Here just for intros?!
- [x] Fix configPage.html delete segemts per type (deletes currently all)
- [x] Move .edl file creation into another [plugin](<https://github.com/endrl/jellyfin-plugin-edl>)
- [ ] Move the extended plugin page for segment edits to a dedicated plugin "Media Segment Editor" with additional meta support per plugin like "get chromaprints of plugin x"

Note: Intro prompt is shown 5 seconds before (plugin default), netflex 2? seconds after. Probably use 3 seconds before as guideline? \
Note: Intro prompt hide is 10 seconds after start. netflix is 8s in total? Probably use 7 seconds after as guideline?
Note: SecondsOfIntroToPlay. Generic offset to prevent skipped content. Should be added to a segment? Or frontend? End-2s as Guideline? \
Note: Show a notification when segment type "Skip" is executed as hint?

List of additional fixes
- [25% or 10 minutes](https://github.com/ConfusedPolarBear/intro-skipper/issues/139) updated to 30% and 15 Minutes
- Skip filter for tv show names and seasons
- Pending: <https://github.com/ConfusedPolarBear/intro-skipper/issues/115>

Analyzes the audio of television episodes to detect intros.

## System requirements
- ~~Jellyfin  10.9 or newer~~

## Introduction requirements

Show introductions will only be detected if they are:
* Located within the first 30% of an episode, or the first 15 minutes, whichever is smaller
* Between 15 seconds and 2 minutes long

Ending credits will only be detected if they are shorter than 4 minutes.

All of these requirements can be customized as needed.

## Installation instructions
1. Add plugin repository to your server: `https://raw.githubusercontent.com/endrl/jellyfin-plugin-repo/master/manifest.json`
2. Install the Media Analyzer plugin from the General section
3. Restart Jellyfin
4. Go to Dashboard -> Scheduled Tasks -> Analyze Episodes and click the play button
