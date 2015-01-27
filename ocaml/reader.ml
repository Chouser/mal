module T = Types.Types
        (* ^file ^module *)

let find_re re str =
  List.map (function | Str.Delim x -> x | Str.Text x -> "impossible!")
    (List.filter (function | Str.Delim x -> true | Str.Text x -> false)
      (Str.full_split re str)) ;;

let token_re = (Str.regexp "~@\\|[][{}()'`~^@]\\|\"\\(\\\\.\\|[^\"]\\)*\"\\|;.*\\|[^][  \n{}('\"`,;)]*")

type reader = {
  form : Types.mal_type;
  tokens : string list;
}

type list_reader = {
  list_form : Types.mal_type list;
  tokens : string list;
}

let read_atom token =
  match token with
    | "nil" -> T.Nil
    | "true" -> T.Bool true
    | "false" -> T.Bool false
    | _ ->
    match token.[0] with
      | '0'..'9' -> T.Int (int_of_string token)
      | '"' -> T.String (Str.global_replace (Str.regexp "\\\\\\(.\\)")
                                                "\\1"
                                                (String.sub token 1 ((String.length token) - 2)))
      | ':' -> T.Keyword (Str.replace_first (Str.regexp "^:") "" token)
      | _ -> Types.symbol token

let with_meta obj meta =
  match obj with
    | T.List   { T.value = value } -> T.List   { T.value = value; T.meta = meta };
    | T.Map    { T.value = value } -> T.Map    { T.value = value; T.meta = meta };
    | T.Vector { T.value = value } -> T.Vector { T.value = value; T.meta = meta };
    | T.Symbol { T.value = value } -> T.Symbol { T.value = value; T.meta = meta };
    | _ -> raise (Invalid_argument "metadata not supported on this type")

let rec read_list list_reader =
  match list_reader.tokens with
    | [] -> output_string stderr "expected ')', got EOF\n";
            flush stderr;
            raise End_of_file;
    | token :: tokens ->
      if Str.string_match (Str.regexp "[])}]") token 0 then
        {list_form = list_reader.list_form; tokens = tokens}
      else
        let reader = read_form list_reader.tokens in
          read_list {list_form = list_reader.list_form @ [reader.form];
                     tokens = reader.tokens}
and read_quote sym tokens =
  let reader = read_form tokens in
    {form = Types.list [ Types.symbol sym; reader.form ];
     tokens = reader.tokens}
and read_form all_tokens =
  match all_tokens with
    | [] -> raise End_of_file;
    | token :: tokens ->
      match token with
        | "'"  -> read_quote "quote" tokens
        | "`"  -> read_quote "quasiquote" tokens
        | "~"  -> read_quote "unquote" tokens
        | "~@" -> read_quote "splice-unquote" tokens
        | "@"  -> read_quote "deref" tokens
        | "^"  ->
           let meta = read_form tokens in
           let value = read_form meta.tokens in
             {form = with_meta value.form meta.form; tokens = value.tokens}
        | "(" ->
           let list_reader = read_list {list_form = []; tokens = tokens} in
             {form = Types.list list_reader.list_form;
              tokens = list_reader.tokens}
        | "{" ->
           let list_reader = read_list {list_form = []; tokens = tokens} in
             {form = Types.list_into_map Types.MalMap.empty list_reader.list_form;
              tokens = list_reader.tokens}
        | "[" ->
           let list_reader = read_list {list_form = []; tokens = tokens} in
             {form = Types.vector list_reader.list_form;
              tokens = list_reader.tokens}
        | _ -> {form = read_atom token; tokens = tokens}

let read_str str = (read_form (List.filter ((<>) "") (find_re token_re str))).form

