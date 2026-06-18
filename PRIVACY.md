# Privacy Policy

**Last updated: 18 June 2026**

This Privacy Policy explains what data the **DesuDesuBot** Discord application
("the Bot", "we") processes, why, and how long it is kept. By adding the Bot to
a server, the server's administrators agree to this policy on behalf of that
server.

The Bot is an automated anti-spam and moderation tool. It only processes data
necessary to detect and act on spam.

## What data we process

The Bot processes the following data from servers (guilds) where it has been
added:

- **Message content** — the text, and basic attachment metadata, of messages
  posted in channels the Bot can see. This is required to detect spam.
- **Message metadata** — message IDs, channel IDs, and timestamps.
- **User identifiers** — the Discord user ID and username of message authors,
  and the server's join date for a member (used to identify new accounts).
- **Server configuration** — per-server settings you set via the `/antispam`
  commands (e.g. alert channel, thresholds, allowed links).
- **Moderation records** — when a message is detected as spam, a record of the
  incident (see retention below).

The Bot does **not** access direct messages, presence/online status, member
lists, or any data from servers it has not been added to.

## How we use the data

Data is used solely to provide the Bot's anti-spam functionality:

- comparing recent messages to detect the same or near-identical message posted
  across multiple channels (spam flooding);
- checking whether newly-joined members post links that are not on the server's
  allow-list;
- taking the moderation actions configured by the server (deleting spam,
  timing out the user, and alerting moderators);
- letting moderators review a flagged incident and choose to ban or release the
  user.

## Storage and retention

- **Recent messages** are held in a temporary in-memory cache used only for the
  similarity check. Each entry expires automatically after approximately
  **one hour** and is never written to long-term storage.
- **Flagged spam incidents** are stored in a database so moderators can review
  and act on them. Each record contains a truncated copy of the message
  (maximum 500 characters), the author's user ID and username, the channels
  involved, and the moderation outcome. These records are **automatically and
  permanently deleted after 7 days.**
- **Server configuration** is stored for as long as the Bot is in your server.

All data is stored on infrastructure controlled by the Bot operator and is not
hosted by Discord.

## What we do not do

- We do **not** sell, rent, or share your data with any third party.
- We do **not** use message content to train machine-learning or AI models.
- We do **not** retain message content beyond what is described above.
- We do **not** track users across servers or build profiles of users.

## Privileged intents

The Bot uses Discord's **Message Content** privileged intent, which is required
to read message text for spam detection. It does **not** use the Server Members
or Presence privileged intents.

## Data deletion

- Incident records are deleted automatically after 7 days, and the temporary
  message cache clears within about an hour.
- Removing the Bot from your server stops all further data processing for that
  server.
- To request deletion of a server's configuration, or to ask any question about
  this policy, open an issue on our GitHub repository
  (<https://github.com/DesuDesu1/AntiSpamDiscordBot>), contact the Bot operator
  on Discord at **@nanashi1725**, or email **ddesuone@gmail.com**.

## Children

The Bot is intended for use in accordance with Discord's Terms of Service and is
not directed at anyone under the minimum age required to use Discord.

## Changes to this policy

We may update this policy as the Bot changes. Material changes will be reflected
by updating the "Last updated" date above. Continued use of the Bot after a
change constitutes acceptance of the updated policy.
