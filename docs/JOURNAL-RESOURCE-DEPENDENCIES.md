# Parent/child resource ownership for journal composition

The existing relative-file state machine now supports `OpenDependent` with an already captured, held parent attempt. This adds managed ownership enforcement needed by a future directory/lease/journal chain; it adds no second executor, native directory arguments, request names, permission toggle or App/harness entry point.

The child uses a separate existing ownership domain. Before its frame claim or preparation, the parent stores a strong reference to the actual child attempt, which in turn retains its frame and domain. A counter or caller-provided completion flag is insufficient. A parent permits one dependent at a time, with at most eight dependency edges. Already cleaning, refused, released or retained parents cannot acquire a child. Same-domain admission remains busy; newly constructed children can depend only on already-held attempts, preventing ordinary cycles.

## Release and uncertainty

Parent release refuses while a child is registered, without consuming the parent's later legitimate release. Child attachment and the actual parent-release commitment share the same lock, closing the gap between an initial eligibility read and resource cleanup. Callbacks still run outside state locks. Cleanup of the child's actual frame finishes before its exact-reference dependency slot detaches; stale released-child calls cannot detach a replacement child.

| Observed child state | Effect on parent |
| --- | --- |
| Pre-entry stop/refusal or failed capture, with known prefix/resource cleanup | Detaches; parent may later release. Resource cleanup is not rollback or absence of a created file. |
| Captured output still held, or preparation/invocation/capture/cleanup callback active | Parent remains held and cannot release. Active work is visible through ancestors. |
| Nonzero/pending/throwing invocation, or unsuccessful/uncertain cleanup | Child and actual parent chain stay retained; no retry, replacement or reset is provided. |

`RequiresRetention` propagates independently of `HasPendingWork`. A returned pending native status can leave no managed callback active while ownership remains uncertain. The parent's own `State` remains Held when its actual resource is held; descendant uncertainty is not relabeled as a fabricated parent native result.

The caller must still root the actual root domain and original parent-tree owner. Within that ownership, the parent roots abandoned descendant frames/domains even if the caller drops the child task/result. Known cleanup detaches a child; retained uncertainty does not. This is cooperating in-process lifetime enforcement, not hostile-code isolation.

## Claim ceiling

An ownership dependency does **not** establish a native `RootDirectory` relation, filesystem identity, namespace security, effective access, journal validity or dispatch permission. The actual native factory must still bind the exact qualified handle/path/request and preserve its resource graph. The existing file vocabulary is unchanged; directory `EnsureExists`/`FILE_OPEN_IF` and actual private-store integration remain missing. All native/production gates and existing factories remain unchanged and false.

Portable tests exercise callback reentrancy, actual cleanup order, cancellation, unknown states, competing children, stale releases, depth, GC rooting and an owned FileStream that must remain readable through child readback. Simulated native statuses are labelled as such. These tests run no Windows native operation, directory creation profile, maintenance or user-PC action. Windows source-bound qualification and the old frozen neutral test remain separate.
