//// Two asks about a Site, as JSON lines.
////
//// `check_site_files`: a Site's declarations and what was read from its files
//// and from systemd, in; the findings, out.
////
//// ```text
//// {"id":"1","ask":"check_site_files","site":"autodl-bj-5090",
////  "profile":"<the execution profile's JSON text>",
////  "supervisor":"<the supervisor declaration's JSON text>" | null,
////  "ssh_include":{"path","text"} | null, "ssh_config":{"path","text"} | null,
////  "unit":{"path","text"} | null, "drop_ins":[{"path","text"}],
////  "loaded":[{"name","temporary","active_state","sub_state",
////             "need_daemon_reload","exec_start":[[word]],"environment":{name:value}}]}
//// ```
////
//// `check_site_connection`: the declarations' text, what is known of the
//// Site's office, the runtime binary if named, and the answers so far; the
//// next question to ask the Site, or the verdict.
////
//// ```text
//// {"id","ask":"check_site_connection","profile","supervisor",
////  "office":"held"|"free"|"unread","binary":null|path,
////  "answers":[{"probe":{"kind":"reach_site"|"home_from_site"|"binary_on_site",
////              "through_forward"?,"path"?},
////              "answer":{"kind":"answered"}|{"kind":"failed","detail"}}]}
//// ```
////
//// `named_paths` (slice F6): every loaded fern unit, every Site's two
//// declarations, and the directories the installer cleans; every path under
//// those directories that a unit or a declaration names, which the cleanup
//// keeps. A declaration that cannot be read is named in `unread`, so the
//// caller refuses to clean rather than clean without it.
////
//// ```text
//// {"id","ask":"named_paths","roots":[dir],"loaded":[loaded unit as above],
////  "declarations":[{"profile","supervisor"}]}
//// -> {"id","answer":{"paths":[path],"unread":[detail]}}
//// ```
////
//// The declarations travel as the text of their files, so that reading them
//// — and refusing a field the declaration does not have — is done here, once.

import fern_scheduler/site_connection
import fern_scheduler/site_files.{
  type Finding, type LoadedUnit, FileText, LoadedUnit, Observed,
}
import gleam/dict
import gleam/dynamic/decode
import gleam/int
import gleam/json
import gleam/list
import gleam/option.{None, Some}
import gleam/result
import gleam/string

pub fn claims(line: String) -> Bool {
  case ask_of(line) {
    Ok("check_site_files")
    | Ok("check_site_connection")
    | Ok("update_site_ssh")
    | Ok("update_site_unit")
    | Ok("named_paths") -> True
    _ -> False
  }
}

fn ask_of(line: String) -> Result(String, json.DecodeError) {
  json.parse(string.trim(line), decode.at(["ask"], decode.string))
}

/// One ask in, one answer out. An ask without a string id is refused without
/// an answer line, as every other ask of this program is.
pub fn answer(document: String) -> Result(String, String) {
  let line = string.trim(document)
  case json.parse(line, decode.at(["id"], decode.string)) {
    Error(_) -> Error("the request requires a string id")
    Ok(id) -> {
      let reply = case ask_of(line) {
        Ok("check_site_connection") -> connection_reply(line)
        Ok("update_site_ssh") -> ssh_update_reply(line)
        Ok("update_site_unit") -> unit_update_reply(line)
        Ok("named_paths") -> named_paths_reply(line)
        _ -> files_reply(line)
      }
      Ok(json.to_string(json.object([#("id", json.string(id)), reply])))
    }
  }
}

fn files_reply(line: String) -> #(String, json.Json) {
  case json.parse(line, ask_decoder()) {
    Ok(#(site, profile, supervisor, observed)) -> {
      let declared = case supervisor {
        Some(text) -> site_files.declare(profile, text)
        None ->
          Error(
            "no supervisor declaration config/profiles/"
            <> site
            <> ".supervisor.json",
          )
      }
      let own = site_files.unit_name(site)
      // CHG 2026-09-25 (slice F2): the check a unit runs before it starts
      // (`before_start`) does not count that unit's own state against it: it
      // is starting, so it is not running yet.
      let before_start =
        json.parse(line, {
          use flag <- decode.optional_field("before_start", False, decode.bool)
          decode.success(flag)
        })
        |> result.unwrap(False)
      let findings =
        site_files.check(declared, observed, own)
        |> list.filter(fn(finding) {
          case finding, before_start {
            site_files.NotRunning(unit, _), True if unit == own -> False
            site_files.Restarted(unit, _), True if unit == own -> False
            _, _ -> True
          }
        })
      #(
        "answer",
        json.object([#("findings", json.array(findings, finding_json))]),
      )
    }
    Error(_) -> invalid("the Site's files could not be read from the request")
  }
}

fn named_paths_reply(line: String) -> #(String, json.Json) {
  let decoder = {
    use roots <- decode.field("roots", decode.list(decode.string))
    use loaded <- decode.field("loaded", decode.list(loaded_unit()))
    use declarations <- decode.field(
      "declarations",
      decode.list({
        use profile <- decode.field("profile", decode.string)
        use supervisor <- decode.field("supervisor", decode.string)
        decode.success(#(profile, supervisor))
      }),
    )
    decode.success(#(roots, loaded, declarations))
  }
  case json.parse(line, decoder) {
    Error(_) ->
      invalid("the named-paths ask could not be read from the request")
    Ok(#(roots, loaded, texts)) -> {
      let read =
        list.map(texts, fn(pair) { site_files.declare(pair.0, pair.1) })
      let declared = result.values(read)
      let unread =
        list.filter_map(read, fn(one) {
          case one {
            Error(detail) -> Ok(detail)
            Ok(_) -> Error(Nil)
          }
        })
      #(
        "answer",
        json.object([
          #(
            "paths",
            json.array(
              site_files.named_paths(loaded, declared, roots),
              json.string,
            ),
          ),
          #("unread", json.array(unread, json.string)),
        ]),
      )
    }
  }
}

fn invalid(detail: String) -> #(String, json.Json) {
  #(
    "error",
    json.object([
      #("kind", json.string("invalid_request")),
      #("detail", json.string(detail)),
    ]),
  )
}

fn connection_reply(line: String) -> #(String, json.Json) {
  case json.parse(line, connection_decoder()) {
    Error(_) ->
      invalid("the connection check could not be read from the request")
    Ok(#(profile, supervisor, supervisor_term, binary, answers)) ->
      case site_files.declare(profile, supervisor) {
        Error(detail) -> invalid(detail)
        Ok(declaration) -> #(
          "answer",
          json.object([
            #(
              "step",
              // CHG 2026-09-25 (slice F2): without a named binary, the
              // declared one is looked for: it is what the unit starts.
              step_json(site_connection.next(
                declaration,
                supervisor_term,
                option.or(binary, Some(declaration.fern_run)),
                answers,
              )),
            ),
            // How the caller addresses the Site, so it reads no declaration.
            #(
              "ssh_target",
              json.nullable(
                option.map(declaration.address, fn(address) {
                  site_files.alias(declaration.site)
                  <> ":"
                  <> int.to_string(address.port)
                }),
                json.string,
              ),
            ),
            #(
              "home",
              json.string(site_files.home_on_site(declaration.remote_forward)),
            ),
            #("python", json.string(declaration.python)),
          ]),
        )
      }
  }
}

fn connection_decoder() -> decode.Decoder(
  #(
    String,
    String,
    site_connection.SupervisorAvailability,
    option.Option(String),
    List(#(site_connection.Probe, site_connection.Answer)),
  ),
) {
  use profile <- decode.field("profile", decode.string)
  use supervisor <- decode.field("supervisor", decode.string)
  use supervisor_term <- decode.field("office", {
    use word <- decode.then(decode.string)
    case word {
      "held" -> decode.success(site_connection.SupervisorRunning)
      "free" -> decode.success(site_connection.NoSupervisorRunning)
      "unread" -> decode.success(site_connection.SupervisorStatusUnknown)
      _ -> decode.failure(site_connection.SupervisorStatusUnknown, "held, free or unread")
    }
  })
  use binary <- decode.field("binary", decode.optional(decode.string))
  use answers <- decode.field(
    "answers",
    decode.list({
      use probe <- decode.field("probe", probe_decoder())
      use answer <- decode.field("answer", {
        use kind <- decode.field("kind", decode.string)
        case kind {
          "answered" -> decode.success(site_connection.Answered)
          "failed" -> {
            use detail <- decode.field("detail", decode.string)
            decode.success(site_connection.Failed(detail))
          }
          _ -> decode.failure(site_connection.Answered, "answered or failed")
        }
      })
      decode.success(#(probe, answer))
    }),
  )
  decode.success(#(profile, supervisor, supervisor_term, binary, answers))
}

fn probe_decoder() -> decode.Decoder(site_connection.Probe) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "reach_site" -> decode.success(site_connection.ReachSite)
    "home_from_site" -> {
      use forward <- decode.field("through_forward", decode.bool)
      decode.success(site_connection.HomeFromSite(forward))
    }
    "binary_on_site" -> {
      use path <- decode.field("path", decode.string)
      decode.success(site_connection.BinaryOnSite(path))
    }
    _ ->
      decode.failure(
        site_connection.ReachSite,
        "reach_site, home_from_site or binary_on_site",
      )
  }
}

fn probe_json(probe: site_connection.Probe) -> json.Json {
  case probe {
    site_connection.ReachSite ->
      json.object([#("kind", json.string("reach_site"))])
    site_connection.HomeFromSite(forward) ->
      json.object([
        #("kind", json.string("home_from_site")),
        #("through_forward", json.bool(forward)),
      ])
    site_connection.BinaryOnSite(path) ->
      json.object([
        #("kind", json.string("binary_on_site")),
        #("path", json.string(path)),
      ])
  }
}

fn step_json(step: site_connection.Step) -> json.Json {
  case step {
    site_connection.Ask(probe) ->
      json.object([#("kind", json.string("ask")), #("probe", probe_json(probe))])
    site_connection.Reached(notes) ->
      json.object([
        #("kind", json.string("reached")),
        #("notes", json.array(notes, json.string)),
      ])
    site_connection.NotReached(sentence) ->
      json.object([
        #("kind", json.string("not_reached")),
        #("sentence", json.string(sentence)),
      ])
  }
}

fn finding_json(finding: Finding) -> json.Json {
  json.object([
    #("kind", json.string(site_files.kind(finding))),
    #("sentence", json.string(site_files.describe(finding))),
  ])
}

fn ask_decoder() -> decode.Decoder(
  #(String, String, option.Option(String), site_files.Observed),
) {
  use site <- decode.field("site", decode.string)
  use profile <- decode.field("profile", decode.string)
  use supervisor <- decode.field("supervisor", decode.optional(decode.string))
  use ssh_include <- decode.field("ssh_include", decode.optional(file()))
  use ssh_config <- decode.field("ssh_config", decode.optional(file()))
  use unit <- decode.field("unit", decode.optional(file()))
  use drop_ins <- decode.field("drop_ins", decode.list(file()))
  use loaded <- decode.field("loaded", decode.list(loaded_unit()))
  decode.success(#(
    site,
    profile,
    supervisor,
    Observed(ssh_include, ssh_config, unit, drop_ins, loaded),
  ))
}

fn file() -> decode.Decoder(site_files.FileText) {
  use path <- decode.field("path", decode.string)
  use text <- decode.field("text", decode.string)
  decode.success(FileText(path, text))
}

fn loaded_unit() -> decode.Decoder(LoadedUnit) {
  use name <- decode.field("name", decode.string)
  use temporary <- decode.field("temporary", decode.bool)
  use active <- decode.field("active_state", decode.string)
  use sub <- decode.field("sub_state", decode.string)
  use reload <- decode.field("need_daemon_reload", decode.bool)
  use starts <- decode.field(
    "exec_start",
    decode.list(decode.list(decode.string)),
  )
  use environment <- decode.field(
    "environment",
    decode.dict(decode.string, decode.string),
  )
  use restarts <- decode.optional_field("restarts", 0, decode.int)
  decode.success(LoadedUnit(
    name,
    temporary,
    active,
    sub,
    reload,
    starts,
    dict.to_list(environment)
      |> list.sort(fn(a, b) { string.compare(a.0, b.0) }),
    restarts,
  ))
}

/// `update_site_ssh`: every declared Site's two declarations and the text of
/// `~/.ssh/config`; the include file's text, the config's new text, and what
/// was removed or kept. A Site whose declarations cannot be read is refused by
/// name and left out.
fn ssh_update_reply(line: String) -> #(String, json.Json) {
  let decoder = {
    use declarations <- decode.field(
      "declarations",
      decode.list({
        use site <- decode.field("site", decode.string)
        use profile <- decode.field("profile", decode.string)
        use supervisor <- decode.field("supervisor", decode.string)
        decode.success(#(site, site_files.declare(profile, supervisor)))
      }),
    )
    use config <- decode.field("config", decode.string)
    use provenance <- decode.field("provenance", decode.string)
    decode.success(#(declarations, config, provenance))
  }
  case json.parse(line, decoder) {
    Error(_) -> invalid("the SSH update could not be read from the request")
    Ok(#(declarations, config, provenance)) -> {
      let declared = list.filter_map(declarations, fn(pair) { pair.1 })
      let refused =
        list.filter_map(declarations, fn(pair) {
          case pair.1 {
            Ok(_) -> Error(Nil)
            Error(detail) -> Ok(#(pair.0, detail))
          }
        })
      let update = site_files.update_ssh(declared, config, provenance)
      #(
        "answer",
        json.object([
          #("include", json.string(update.include_text)),
          #("config", json.string(update.config_text)),
          #("include_added", json.bool(update.include_added)),
          #("removed", json.array(update.removed, json.string)),
          #(
            "kept",
            json.array(update.kept, fn(pair) {
              json.object([
                #("alias", json.string(pair.0)),
                #("reason", json.string(pair.1)),
              ])
            }),
          ),
          #(
            "refused",
            json.array(refused, fn(pair) {
              json.object([
                #("site", json.string(pair.0)),
                #("detail", json.string(pair.1)),
              ])
            }),
          ),
          #(
            "aliases",
            json.array(declared, fn(declaration) {
              json.string(site_files.alias(declaration.site))
            }),
          ),
        ]),
      )
    }
  }
}

/// `update_site_unit`: what `check_site_files` reads, and the unit's inputs
/// (`session_program`, `working_directory`, `checks_before_start`,
/// `provenance`) and `remove_extra`; the rendered unit and what to do with it,
/// or the refusal as one sentence. Nothing here writes.
fn unit_update_reply(line: String) -> #(String, json.Json) {
  let decoder = {
    use inputs <- decode.field("inputs", {
      use path <- decode.field("path", decode.string)
      use session <- decode.field("session_program", decode.string)
      use directory <- decode.field("working_directory", decode.string)
      use checks <- decode.field(
        "checks_before_start",
        decode.list(decode.list(decode.string)),
      )
      use provenance <- decode.field("provenance", decode.string)
      decode.success(site_files.UnitInputs(
        path,
        session,
        directory,
        checks,
        provenance,
      ))
    })
    use remove_extra <- decode.field("remove_extra", decode.bool)
    decode.success(#(inputs, remove_extra))
  }
  case json.parse(line, ask_decoder()), json.parse(line, decoder) {
    Ok(#(site, profile, supervisor, observed)), Ok(#(inputs, remove_extra)) -> {
      let declared = case supervisor {
        Some(text) -> site_files.declare(profile, text)
        None ->
          Error(
            "no supervisor declaration config/profiles/"
            <> site
            <> ".supervisor.json",
          )
      }
      let decided =
        result.try(declared, fn(declaration) {
          site_files.update_unit(declaration, observed, inputs, remove_extra)
        })
      #("answer", case decided {
        Error(sentence) ->
          json.object([
            #("kind", json.string("refused")),
            #("sentence", json.string(sentence)),
          ])
        Ok(update) ->
          json.object([
            #("kind", json.string("update")),
            #("text", json.string(update.text)),
            #("write", json.bool(update.write)),
            #("remove", json.array(update.remove, json.string)),
            #("reload", json.bool(update.reload)),
            #(
              "changes",
              json.array(update.changes, fn(change) {
                json.object([
                  #("fact", json.string(change.0)),
                  #("now", json.string(change.1)),
                  #("after", json.string(change.2)),
                ])
              }),
            ),
          ])
      })
    }
    _, _ -> invalid("the unit update could not be read from the request")
  }
}
