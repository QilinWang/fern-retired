//// Which Site runs a cell, as a program the C# scheduler holds open and asks.
////
//// One JSON document per line in, one per line out, for as long as the
//// scheduler that started it runs. The asks are `choose`, `read_offer` and
//// `read_demand`, and every answer carries back the `id` its ask arrived
//// with, so a caller can tell one answer from another.
////
//// **Held open, not started per question.** Starting an Erlang virtual
//// machine costs a fifth of a second. A scheduler that started one per cycle
//// would pay that forever, for nothing; the Site's program is held the same
//// way, for the same reason, and the transport on the C# side is the one
//// class (`GleamProgram`) serving both.
////
//// If this program cannot be started, the comparison on the other side
//// fails: a missing answer is not a matching one.
////
//// CHG 2026-09-25: two more asks, `explain` and `cycle`, answered by
//// `cycle_wire`. A `cycle` ask belongs to one supervisor's round, whose
//// memory this loop keeps between lines; every other ask still keeps nothing.
//// A third, `check_site_files`, is answered by `site_files_wire`.
//// CHG 2026-09-26: `office_lease` (slice W1 of leases-and-watching-a-running-cell),
//// answered by `office_lease_wire`; the state travels with the ask.

import fern_scheduler/cycle_wire
import fern_scheduler/office_lease_wire
import fern_scheduler/site_files_wire
import fern_scheduler/wire
import gleam/io

pub fn main() -> Nil {
  serve(cycle_wire.memory())
}

fn serve(memory: cycle_wire.Memory) -> Nil {
  case wire.read_line() {
    // Standard input ended: whoever held this open has gone.
    Error(Nil) -> Nil
    Ok(line) -> {
      let #(memory, said) = case
        cycle_wire.claims(line),
        site_files_wire.claims(line),
        office_lease_wire.claims(line)
      {
        True, _, _ -> cycle_wire.answer(memory, line)
        _, True, _ -> #(memory, site_files_wire.answer(line))
        _, _, True -> #(memory, office_lease_wire.answer(line))
        _, _, _ -> #(memory, wire.answer(line))
      }
      case said {
        Ok(said) -> io.println(said)
        // A line this program cannot read is said so and the next one is
        // read: one bad ask does not end a scheduler's whole run.
        Error(said) -> io.println_error("fern_scheduler: " <> said)
      }
      serve(memory)
    }
  }
}
