# EspiGamba Helper

A Dalamud plugin meant to help Espi Gamba Dealers with annoying tasks

## Stats

The Stats tab keeps a local SQLite database (`stats.db` in the plugin's config folder) of every blackjack round,
scratch game and wheel game, and shows the combined profit for a timeframe (last 12 hours by default, or any
range picked with the two date-time selectors).

- **Live recording** subscribes to the game-ended events of SimpleBlackjack, SimpleScratch and SimpleWheel. Games
  announced live count as hosted by the logged-in character.
- **Import from plugins** reads the archives of all three plugins over IPC and adds the games that are not recorded yet.
  Scratch and wheel archives are shared between every dealer on a theme, so imported games keep their dealer and the
  tab only shows the ones the logged-in character hosted. Blackjack has no shared stats and is always shown.
