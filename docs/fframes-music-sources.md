# Music discovery for video agents

Read this before searching for or replacing music in fframes/help-video work. This is a discovery guide, not blanket permission for a library. Official source policies were checked on 2026-10-25; verify the actual track, license and publication destinations when selecting it. Search terms below are starting points, not auditioned recommendations. "Royalty-free" does not mean free of charge or free of copyright.

## Start with the video brief

Treat product promotion, client work and monetized videos as commercial use. Establish destinations, duration, voice-over, mood, energy and whether attribution can appear in the video/description. Prefer instrumental tracks without prominent lead melodies under narration.

| Need | Search first | Useful search terms |
|---|---|---|
| Narrated tutorial or screencast | Mixkit; ccMixter commercial section | ambient, minimal electronic, soft lo-fi, instrumental, no vocals |
| Short feature demo or upbeat montage | Mixkit; Incompetech | house, funk, synthwave, upbeat electronic |
| Cinematic introduction or project story | Scott Buckley's original library | cinematic, piano, ambient, orchestral, sci-fi |
| Comedy, playful transitions or character scenes | Incompetech | comedy, quirky, playful, jazz |
| Less conventional indie, folk, jazz or experimental music | ccMixter; Free Music Archive | acoustic, folk, jazz, downtempo, experimental |
| YouTube-only publishing | Official YouTube Studio Audio Library | filter genre, mood, duration and attribution |

## Sources and limits

- **[Mixkit](https://mixkit.co/free-stock-music/)**: broad genre catalog; no mandatory attribution or signup. Its [Stock Music Free License](https://mixkit.co/license/modal/musicFree/) permits commercial/noncommercial web and social video, podcasts, education and online ads. Not for games, TV/radio, CDs/DVDs. Do not remix its tracks, incorporate them into music-only tracks, claim ownership or register them with a rights-management service. Editing/synchronization must follow the actual license; do not infer remix permission from permission to use music in video.
- **[Incompetech / Kevin MacLeod](https://incompetech.com/music/royalty-free/music.html)**: varied instrumental music, including comedy and electronic. [FAQ and credit template](https://incompetech.com/music/royalty-free/faq.html): free CC BY 4.0 use with attribution, including monetized video; a paid license is needed if attribution cannot be given. Copy the selected track's credit, not a generic artist-only line.
- **[Scott Buckley](https://www.scottbuckley.com.au/library/)**: cinematic, orchestral, ambient and piano. Original library tracks are CC BY 4.0 unless stated otherwise; commercial projects are allowed with credit. His remixes are not included in that permission. Follow [Using This Music](https://www.scottbuckley.com.au/library/using-this-music/) and [FAQ](https://www.scottbuckley.com.au/library/faq/); YouTube credit must be in the description, not only on screen. Smart Content ID can claim videos lacking that credit.
- **[dig ccMixter: Free for Commercial Use](https://dig.ccmixter.org/free)**: community remixes, electronic, hip-hop, downtempo, jazz, acoustic and folk; includes an instrumental-only filter. Use this commercial section, then check the individual license and all required musician/sample credits. The [license guide](https://dig.ccmixter.org/licenses) distinguishes free commercial use with credit from noncommercial-only and paid options.
- **[Free Music Archive](https://freemusicarchive.org/genres)**: independent artists across genres. Use commercial-use AND video/remix filters; default to CC0 or CC BY, then inspect the track page. [Video FAQ](https://freemusicarchive.org/FAQ_For_Videos) explains NC, ND and SA restrictions and possible Content ID claims. Its published attribution guidance asks for credit in the video and accompanying description; preserve that placement in the delivery notes. This is source guidance, not a universal extra CC BY term.
- **[Pixabay Music](https://pixabay.com/music/)**: broad background-music catalog, no mandatory attribution. The [Music FAQ](https://pixabay.com/service/faq/) permits commercial/client/promotional videos when music is part of a larger creative work, not a standalone distribution. Some contributors register tracks in Content ID, so legal use can still receive a claim. Retain download evidence and a license certificate if available; do not promise one is issued for every track.
- **[Official YouTube Audio Library](https://www.youtube.com/audiolibrary)**: available through YouTube Studio; genre/mood/duration filters and attribution-required/not-required options. [YouTube Help](https://support.google.com/youtube/answer/3376882?hl=en) permits monetization and states official-library downloads will not receive rights-holder Content ID claims on YouTube. Copy CC credits into the description; the automatic "Music in this Video" block is not a substitute. Do not extend this assurance to other destinations or channels merely named "Audio Library"; check the selected license for off-platform use.
- **[NCS / NoCopyrightSounds](https://ncs.io/usage-policy)**: useful for energetic electronic music, but not the default for product promotion. Its free creator policy covers independent-creator UGC with the required artist/track credit, including eligible sponsored/paid-ad creator content. A brand/service promoting its own goods, service or product must contact NCS partnerships for permission; obtain and retain the project-specific terms before use.

## License decision

- [CC0](https://creativecommons.org/publicdomain/zero/1.0/): no mandatory attribution; keep provenance anyway. Verify the uploader is the original source and that the dedication covers the recording, not only the composition.
- [CC BY](https://creativecommons.org/licenses/by/4.0/): commercial video and adaptations are permitted with appropriate credit, license/source links, modification notices and supplied notices/attribution parties. Do not imply artist endorsement.
- **NC**: exclude from commercial/product work without separate written permission.
- **ND**: exclude from videos without separate permission; synchronizing music to images is an adaptation even without cutting the track. See [Creative Commons: legal music for videos](https://creativecommons.org/legalmusicforvideos/).
- **SA**: use only when the video can meet the applicable ShareAlike requirements; otherwise choose another track or obtain separate permission.
- Custom library licenses: use their actual terms, not the CC rules above. A free download, "no copyright" label or community recommendation is not a license. For classical works, verify recording rights separately from composition rights.

## Record and deliver a selected track

Before adding a track, retain the original audio plus a saved track/license page or permission receipt. In the film's existing music-provenance record (currently `tools/help-videos/fframes-vpnrouter/licenses/MUSIC.md`), record:

- Title, artist and all supplied attribution parties/notices; original track URL and download URL.
- Exact license/version and URL, date checked/downloaded, commercial/video permission and intended publication destinations.
- Local filename and SHA-256 of the downloaded bytes; license/download certificate or permission receipt if available.
- Ready-to-publish credit and required placement (video credits, description, or both). Retain supplied copyright/license/disclaimer notices and any prior modification notice.
- Edits such as trim, loop or fade; publish the required modification notice. Distinguish an edited excerpt from the original recording.
- Known Content ID status and any actual claim-release instructions; label unknown status as unknown.

Before publishing, check that the actual video/description contains the required credits, not just this record. A private/unlisted upload check can detect a current YouTube claim but cannot guarantee future claim-free use. Distinguish a Content ID claim from a copyright strike and use the source's documented release/dispute process with retained evidence.

Do not redistribute a standalone music library, bundle third-party audio in a source ZIP, or register it in Content ID unless the actual license expressly permits the intended action. Some CC licenses permit redistribution; custom stock licenses often do not. Video-sync permission alone does not authorize a redistributable audio pack.

## Existing soundtrack and change boundary

The active film already uses "Interchange" by TRG Banks under CC0, documented in its music-provenance record. Keep it unless the video task calls for different music. The renderer produces a silent visual master; the current export scripts add music afterward. When a soundtrack change is requested, update that existing provenance record and the actual exporter/credits together, then use the owning zone's export checks. This guide does not change audio, introduce an online-search integration, authorize a render, or claim that any agent has auditioned a track.
