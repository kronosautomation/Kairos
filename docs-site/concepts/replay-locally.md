# Replay a production run on your machine

When a run in production does something unexpected, the fastest way to understand it is to step through your workflow code with the
run's real history. `kairos history export` copies one run's history to a file, and `KairosReplayer` replays that file against the
code on your machine, under your debugger.

## Export

```
kairos history export 3f2a9c1e-8d4b-4c47-9a10-5b7e6d2f0a11 --reason "reproduce ticket 4411" --out run.khist
```

- **Exporting is a privileged action.** Through the server's API it needs the operator role or above in the run's namespace. The command line connects to the database
  directly with a data-plane role, because an export is always recorded; a read-only role cannot export. Whoever holds that connection string can export, so keep it to the people
  who may.
- **A reason is required**, up to 500 characters. It is written to the audit log in the same transaction that reads the history, together
  with the hash of the last event exported. There is no export without an audit entry. Say why; do not paste data into it.
- **Who exported is not something you can type.** The audit entry names the database login the server authenticated and the network
  address it saw the connection come from, both written by the database itself. The name you give with `--actor` (or `KAIROS_ACTOR`) is
  stored beside it, labelled as a claim, and changes nothing else. When an export goes through `KairosClient.ExportHistoryAsync` with a
  principal that an authenticator verified, the entry names that principal instead.
- **The file is verified before it is written.** If the stored history does not verify, no file is produced (the attempt is still
  recorded).
- **Payloads stay sealed.** The file holds the history exactly as the database does: with client-side encryption on, ciphertext only.
  Only someone with access to the key can read the payloads, and the file says nothing a database dump would not. Treat it as data from
  production all the same.

`--metadata-only` removes the payloads and failure texts and keeps event types, sequence numbers, names, ids and timestamps. The file
also carries a digest of everything it removed, and a second hash chain over the metadata and those digests, so changing a name,
reordering or dropping an event, or swapping a digest fails `kairos history verify`. Such a file cannot be replayed, because the
payloads are gone.

`kairos history verify run.khist` checks a file and prints the hash of its last event. A hash chain shows that a file is consistent and
unedited since it was made, not who made it. To tie a file to the system, compare that hash with the one in the `ExportHistory` audit
entry.

## Replay

[!code-csharp[](../../samples/Concepts.Tests/ReplayLocallyTests.cs#replay-file)]

The workflow runs in your process, so a breakpoint in it is hit in the order the run originally reached it. **Activities do not run**: the
results recorded in the history are fed back, so nothing is charged, sent or written again. If the application seals payloads, pass a
protector built with your own identity and key provider; a protector without access to the key gets a report that the history is
unreadable, not a guess.

The file is verified again when it is loaded. A history that was changed after export is reported as an integrity failure and nothing is
replayed.

If your code no longer matches the recorded run, the report is the one [`kairos replay-check`](deploy-safety.md) prints: the first event
the code does not match, and what it did instead, by names, ids and sizes only.

```
FAIL  nondeterministic  ship  build=default  run:3f2a9c1e-...  events=6
      first divergence at event 3
      recorded: ActivityScheduled: activity 1 'charge' (7 bytes)
      emitted:  StartTimer: timer 1 for 00:00:05
```

`KairosReplayer.AssertReplaysAsync` throws instead of returning the report, for use in a test, for example to keep a bug's history as a
regression test.

## What a run used

Model steps record the tokens they consumed in their own history event: input, output and cached input tokens, and the model that
answered. The figures are readable metadata, so they can be read without a key, and because they live in the event, replaying a run
never counts a step twice. An attempt that consumed tokens and then failed (a reply that could not be used, say) records its usage on the
failure event, and the run reports it **apart** from the completed steps, so waste is visible and the total consumed is still the sum.
An attempt that reports nothing, and an attempt that was cancelled or whose lease was lost before it finished, records nothing.

[!code-csharp[](../../samples/Concepts.Tests/ReplayLocallyTests.cs#run-usage)]
