import fern_scheduler/wire
import gleam/dynamic/decode
import gleam/json
import gleeunit

pub fn main() -> Nil {
  gleeunit.main()
}

pub fn an_invalid_request_with_an_id_receives_a_correlated_refusal_test() {
  let assert Ok(answer) =
    wire.answer("{\"id\":\"bad-42\",\"ask\":\"not_a_known_request\"}")
  assert json.parse(answer, decode.at(["id"], decode.string)) == Ok("bad-42")
  assert json.parse(answer, decode.at(["error", "kind"], decode.string))
    == Ok("invalid_request")
  let assert Ok(_) =
    wire.answer("{\"id\":\"next-43\",\"ask\":\"known_models\"}")
}

pub fn an_unidentifiable_request_is_refused_without_making_up_an_id_test() {
  let assert Error(_) = wire.answer("{bad json")
  let assert Error(_) = wire.answer("{\"ask\":\"known_models\"}")
  let assert Error(_) = wire.answer("{\"id\":42,\"ask\":\"known_models\"}")
}
