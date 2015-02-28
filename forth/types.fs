require str.fs

\ === sorted-array === /
\ Here are a few utility functions useful for creating and maintaining
\ the deftype* method tables. The keys array is kept in sorted order,
\ and the methods array is maintained in parallel so that an index into
\ one corresponds to an index in the other.

\ Search a sorted array for key, returning the index of where it was
\ found. If key is not in the array, return the index where it would
\ be if added.
: array-find { a-length a-addr key -- index found? }
  0 a-length           ( start end )
  begin
    \ cr 2dup . .
    2dup + 2 / dup     ( start end middle middle )
    cells a-addr + @   ( start end middle mid-val )
    dup key < if
      drop rot         ( end middle start )
      2dup = if
        2drop dup      ( end end )
      else
        drop swap      ( middle end )
      endif
    else
      key > if         ( start end middle )
        nip            ( start middle )
      else
        -rot 2drop dup ( middle middle )
      endif
    endif
  2dup = until
  dup a-length = if
      drop false
  else
      cells a-addr + @ key =
  endif ;

\ Create a new array, one cell in length, initialized the provided value
: new-array { value -- array }
  cell allocate throw value over ! ;

\ Resize a heap-allocated array to be one cell longer, inserting value
\ at idx, and shifting the tail of the array as necessary. Returns the
\ (possibly new) array address
: array-insert { old-array-length old-array idx value -- array }
  old-array old-array-length 1+ cells resize throw
  { a }
  a idx cells +   dup cell+   old-array-length idx - cells   cmove>
  value a idx cells + !
  a
  ;


\ === deftype* -- protocol-enabled structs === /
\ Each type has MalTypeType% struct allocated on the stack, with
\ mutable fields pointing to all class-shared resources, specifically
\ the data needed to allocate new instances, and the table of protocol
\ methods that have been extended to the type.
\ Use 'deftype*' to define a new type, and 'new' to create new
\ instances of that type.

struct
  cell% field mal-type
  cell% field mal-meta
  \ cell% field ref-count \ Ha, right.
end-struct MalType%

struct
  cell% 2 * field MalTypeType-struct
  cell% field MalTypeType-methods
  cell% field MalTypeType-method-keys
  cell% field MalTypeType-method-vals
  cell% field MalTypeType-name-addr
  cell% field MalTypeType-name-len
end-struct MalTypeType%

: new ( MalTypeType -- obj )
  dup MalTypeType-struct 2@ %allocate throw ( MalTypeType obj ) \ create struct
  dup -rot mal-type !                       ( obj ) \ set struct's type pointer to this type
  nil over mal-meta !
  ;

: deftype* ( struct-align struct-len -- MalTypeType )
  MalTypeType% %allot                      ( s-a s-l MalTypeType )
  dup 2swap rot                            ( MalTypeType s-a s-l MalTypeType )
  MalTypeType-struct 2!                    ( MalTypeType ) \ store struct info
  dup MalTypeType-methods     0   swap !   ( MalTypeType )
  dup MalTypeType-method-keys nil swap !   ( MalTypeType )
  dup MalTypeType-method-vals nil swap !   ( MalTypeType )
  dup MalTypeType-name-len    0   swap !   ( MalTypeType )
  ;

\ parse-name uses temporary space, so copy into dictionary stack:
: parse-allot-name { -- new-str-addr str-len }
    parse-name { str-addr str-len }
    here { new-str-addr } str-len allot
    str-addr new-str-addr str-len cmove
    new-str-addr str-len ;

: deftype ( struct-align struct-len R:type-name -- )
    parse-allot-name { name-addr name-len }

    \ allot and initialize type structure
    deftype* { mt }
    name-addr mt MalTypeType-name-addr !
    name-len  mt MalTypeType-name-len  !
    \ ." Defining " mt MalTypeType-name-addr @ mt MalTypeType-name-len @ type cr
    mt name-addr name-len nextname 1 0 const-does> ;

: type-name ( mal-type )
    dup  MalTypeType-name-addr @ ( mal-type name-addr )
    swap MalTypeType-name-len @ ( name-addr name-len )
    ;

\ nil type and instance to support extending protocols to it
MalType% deftype MalNil   MalNil   new constant mal-nil
MalType% deftype MalTrue  MalTrue  new constant mal-true
MalType% deftype MalFalse MalFalse new constant mal-false

: mal-bool
    0= if mal-false else mal-true endif ;

: not-object? ( obj -- bool )
    dup 7 and 0 <> if
        drop true
    else
        1000000 <
    endif ;

\ === protocol methods === /

struct
    cell% field proto-impl/count      \ number of types impl'd for this method
    cell% field proto-impl/types      \ array of types (keys)
    cell% field proto-impl/xts        \ array of implementation xts (vals)
    cell% field proto-impl/default-xt \ xt of impl to use when no matching type
    cell% field proto-impl/nt         \ name token of this protocol method
end-struct proto-impls%

: illegal-proto-impl ." illegal-proto-impl: this should never happen" cr ;
-1 constant illegal-type-id
-2 constant default-type-id

: .src-info space ." file: " sourcefilename safe-type space ." line " sourceline# . ;
: .impl-method-name ( impls ) proto-impl/nt @ name>string safe-type ;
: prev-literal-addr here cell - ;

: get-type ( ... obj -- ... obj type-num )
   mal-type @ ;

: proto-cache-miss ( ... obj impls cached-type-addr cached-xt-addr -- rtn-vals... )
    { obj impls cached-type-addr cached-xt-addr }
    impls proto-impl/count @
    impls proto-impl/types @
    obj get-type dup { type-id } ( count types type-id )
    array-find { idx found? }
    found? if
        impls proto-impl/xts @ idx cells + @
    else
        \ method not extended to this obj's type. try default
        impls proto-impl/default-xt @ dup 0= if ( xt )
          0 0 s" '" type-id type-name s" ' extended to type '"
          impls proto-impl/nt @ name>string s" No protocol fn '" ...throw-str
        endif
    endif ( xt )

    dup cached-xt-addr !
    type-id cached-type-addr !
    obj swap execute ;

\ Compile a call-site for a protocol method
: make-call-site { impls }
    \ user-definable word to get a type id from an instance:
    postpone dup postpone get-type

    \ push cached type-id onto data stack
    illegal-type-id postpone literal
    prev-literal-addr { cached-type }

    \ if instance's type-id equals cached type-id, go to fast path
    postpone = postpone if
      \ push cached method implementation and execute it
      ['] illegal-proto-impl postpone literal
      prev-literal-addr { cached-xt } \ TODO: consider compilation token, skip the execute?
      postpone execute
    postpone else
      \ cache miss. get pointers to everything relevant, push them, and call proto-cache-miss
      impls postpone literal
      cached-type postpone literal
      cached-xt postpone literal
      postpone proto-cache-miss
    postpone endif ;

\ Build a new string with "-impls" appended
: pad-append { addr1 len1 addr2 len2 -- pad len }
    addr1 pad len1 cmove
    addr2 pad len1 + len1 len2 + cmove
    pad len1 len2 + ;

\ Define a new named protocol method
: def-proto-str { base-addr base-len }
    \ Allot a pointer to the type-xt-map, name it with "-impls" suffix
    base-addr base-len s" -impls" pad-append nextname create
    here  proto-impls% %allot { impls }
    impls proto-impls% nip erase
    \ Define <method>-int
    base-addr base-len s" -int" pad-append 2dup nextname
    : 99 postpone literal postpone ;  \ TODO
    find-name name>int ( int-xt )
    \ Define <method>-comp
    base-addr base-len s" -comp" pad-append 2dup nextname
    : impls postpone literal postpone make-call-site postpone ;
    find-name name>int ( int-xt comp-xt )
    \ Define combined word <method>
    base-addr base-len nextname interpret/compile:
    \ Store protocol method name
    base-addr base-len find-name impls proto-impl/nt ! ;

: def-protocol-method parse-name def-proto-str ;

\ Extend a type with a protocol method. This mutates the proto-impls
\ for the given method
: extend-method* { type-id impls ixt -- type-id }
    \ ." Extend '" impls .impl-method-name ." ' to " type-id type-name safe-type .src-info space impls . cr
    \ ." , "
    \ type MalTypeType-methods 2@ ( method-keys methods )
    \ 0 ?do
    \     dup i cells + @ >name name>string safe-type ." , "
    \     \ dup i cells + @ .
    \ loop
    \ drop cr
    type-id default-type-id = if
        impls proto-impl/default-xt @ 0<> if
            ." Warning: overwriting protocol default impl for method "
            impls .impl-method-name .src-info cr
        endif
        ixt impls proto-impl/default-xt !
    else
        impls proto-impl/count @ 0= if
            1 impls proto-impl/count !
            type-id new-array impls proto-impl/types !
            ixt new-array impls proto-impl/xts  !
        else
            impls proto-impl/count @ { old-count }
            old-count impls proto-impl/types @ type-id array-find { idx found? }
            found? if \ overwrite
                ." Warning: overwriting protocol method '" impls .impl-method-name
                ." ' impl on type-id " type-id . .src-info cr
                impls proto-impl/xts @ idx cells + ixt !
            else \ resize
                impls proto-impl/count @ 1+ { new-count }
                new-count impls proto-impl/count !
                old-count impls proto-impl/types @ idx type-id array-insert
                impls proto-impl/types !
                old-count impls proto-impl/xts   @ idx ixt     array-insert
                impls proto-impl/xts !
            endif
        endif
    endif
    type-id ;

: extend ( type-id -- type-id pxt install-xt <noname...>)
    parse-name s" -impls" pad-append find-name ( type-id nt )
    name>int ( type-id xt ) \ xt is interpretations semantics
    execute ( type-id <method>-impls )
    ['] extend-method*
    :noname ;

: ;; ( compile-time-xt <noname...> -- type )
    [compile] ; ( compile-time-xt run-time-xt )
    swap execute
    ; immediate

(
\ These whole-protocol names are only needed for 'satisfies?':
protocol IPrintable
  def-protocol-method pr-str
end-protocol

MalList IPrintable extend
  pr-str-impls :noname drop s" <unprintable>" ; extend-method*

  extend-method pr-str
    drop s" <unprintable>" ;;
end-extend
)

\ === Mal types and protocols === /

def-protocol-method conj ( obj this -- this )
def-protocol-method assoc ( k v this -- this )
def-protocol-method dissoc ( k this -- this )
def-protocol-method get ( not-found k this -- value )
def-protocol-method mal= ( a b -- bool )
def-protocol-method as-native ( obj --  )

def-protocol-method to-list ( obj -- mal-list )
def-protocol-method empty? ( obj -- mal-bool )
def-protocol-method mal-count ( obj -- mal-int )
def-protocol-method sequential? ( obj -- mal-bool )
def-protocol-method get-map-hint ( obj -- hint )
def-protocol-method set-map-hint! ( hint obj -- )


\ Fully evalutate any Mal object:
def-protocol-method mal-eval ( env ast -- val )

\ Invoke an object, given whole env and unevaluated argument forms:
def-protocol-method eval-invoke ( env list obj -- ... )

\ Invoke a function, given parameter values
def-protocol-method invoke ( argv argc mal-fn -- ... )


: m= ( a b -- bool )
    2dup = if
        2drop true
    else
        mal=
    endif ;


MalType%
  cell% field MalInt/int
deftype MalInt

: MalInt. { int -- mal-int }
    MalInt new dup MalInt/int int swap ! ;

MalInt
  extend mal= ( other this -- bool )
    over mal-type @ MalInt = if
        MalInt/int @ swap MalInt/int @ =
    else
        2drop 0
    endif ;;

  extend as-native ( mal-int -- int )
    MalInt/int @ ;;
drop


MalType%
  cell% field MalList/count
  cell% field MalList/start
deftype MalList

: MalList. ( start count -- mal-list )
    MalList new
    swap over MalList/count ! ( start list )
    swap over MalList/start ! ( list ) ;

: here>MalList ( old-here -- mal-list )
    here over - { bytes } ( old-here )
    MalList new bytes ( old-here mal-list bytes )
    allocate throw dup { target } over MalList/start ! ( old-here mal-list )
    bytes cell / over MalList/count ! ( old-here mal-list )
    swap target bytes cmove ( mal-list )
    0 bytes - allot \ pop list contents from dictionary stack
    ;

: MalList/concat ( list-of-lists )
    dup MalList/start @ swap MalList/count @ { lists argc }
    0   lists argc cells +  lists  +do ( count )
        i @ to-list MalList/count @ +
    cell +loop { count }
    count cells allocate throw { start }
    start   lists argc cells +  lists  +do ( target )
        i @ to-list MalList/count @ cells  2dup  i @ to-list MalList/start @  -rot  ( target bytes src target bytes )
        cmove ( target bytes )
        + ( new-target )
    cell +loop
    drop start count MalList. ;

MalList
  extend to-list ;;
  extend sequential? drop mal-true ;;
  extend conj { elem old-list -- list }
    old-list MalList/count @ 1+ { new-count }
    new-count cells allocate throw { new-start }
    elem new-start !
    new-count 1 > if
      old-list MalList/start @   new-start cell+   new-count 1- cells  cmove
    endif
    new-start new-count MalList. ;;
  extend empty? MalList/count @ 0= mal-bool ;;
  extend mal-count MalList/count @ MalInt. ;;
  extend mal=
    over mal-nil = if
        2drop false
    else
        swap to-list dup 0= if
            nip
        else
            2dup MalList/count @ swap MalList/count @ over = if ( list-a list-b count )
                -rot MalList/start @ swap MalList/start @ { start-b start-a }
                true swap ( return-val count )
                0 ?do
                    start-a i cells + @
                    start-b i cells + @
                    m= if else
                        drop false leave
                    endif
                loop
            else
                drop 2drop false
            endif
        endif
    endif ;;
drop

MalList new 0 over MalList/count ! constant MalList/Empty

: MalList/rest { list -- list }
    list MalList/start @   cell+
    list MalList/count @   1-
    MalList. ;


MalType%
  cell% field MalVector/list
deftype MalVector

MalVector
  extend sequential? drop mal-true ;;
  extend to-list
    MalVector/list @ ;;
  extend empty?
    MalVector/list @
    MalList/count @ 0= mal-bool ;;
  extend mal-count
    MalVector/list @
    MalList/count @ MalInt. ;;
  extend mal=
    MalVector/list @ swap m= ;;
  extend conj
    MalVector/list @ { elem old-list }
    old-list MalList/count @ { old-count }
    old-count 1+ cells allocate throw { new-start }
    elem new-start old-count cells + !
    old-list MalList/start @   new-start old-count cells  cmove
    new-start   old-count 1+  MalList.
    MalVector new swap
    over MalVector/list ! ;;
drop

MalType%
  cell% field MalMap/list
deftype MalMap

MalMap new MalList/Empty over MalMap/list ! constant MalMap/Empty

: MalMap/get-addr ( k map -- addr-or-nil )
    MalMap/list @
    dup MalList/start @
    swap MalList/count @ { k start count }
    true \ need to search?
    k get-map-hint { hint-idx }
    hint-idx -1 <> if
        hint-idx count < if
            hint-idx cells start + { key-addr }
            key-addr @ k m= if
                key-addr cell+
                nip false
            endif
        endif
    endif
    if \ search
        nil ( addr )
        count cells start +  start  +do
            i @ k m= if
                drop i
                dup start - cell / k set-map-hint!
                cell+ leave
            endif
        [ 2 cells ] literal +loop
    endif ;

MalMap
  extend conj ( kv map -- map )
    MalMap/list @ \ get list
    over MalList/start @ cell+ @ swap conj \ add value
    swap MalList/start @ @ swap conj \ add key
    MalMap new dup -rot MalMap/list ! \ put back in map
    ;;
  extend assoc ( k v map -- map )
    MalMap/list @ \ get list
    conj conj
    MalMap new tuck MalMap/list ! \ put back in map
    ;;
  extend dissoc { k map -- map }
    map MalMap/list @
    dup MalList/start @ swap MalList/count @ { start count }
    map \ return original if key not found
    count 0 +do
        start i cells + @ k mal= if
            drop here
            start i MalList. ,
            start i 2 + cells +  count i - 2 - MalList. ,
            here>MalList MalList/concat
            MalMap new dup -rot MalMap/list ! \ put back in map
        endif
    2 +loop ;;
  extend get ( not-found k map -- value )
    MalMap/get-addr ( not-found addr-or-nil )
    dup 0= if drop else nip @ endif ;;
  extend empty?
    MalMap/list @
    MalList/count @ 0= mal-bool ;;
  extend mal-count
    MalMap/list @
    MalList/count @ 2 / MalInt. ;;
drop

\ Examples of extending existing protocol methods to existing type
default-type-id
  extend conj   ( obj this -- this )
    nip ;;
  extend to-list drop 0 ;;
  extend empty? drop mal-true ;;
  extend sequential? drop mal-false ;;
  extend mal= = ;;
  extend get-map-hint drop -1 ;;
  extend set-map-hint! 2drop ;;
drop

MalNil
  extend conj ( item nil -- mal-list )
    drop MalList/Empty conj ;;
  extend as-native drop nil ;;
  extend get 2drop ;;
  extend to-list drop MalList/Empty ;;
  extend empty? drop mal-true ;;
  extend mal-count drop 0 MalInt. ;;
  extend mal= drop mal-nil = ;;
drop

MalType%
  cell% field MalSymbol/sym-addr
  cell% field MalSymbol/sym-len
  cell% field MalSymbol/map-hint
deftype MalSymbol

: MalSymbol. { str-addr str-len -- mal-sym }
    MalSymbol new { sym }
    str-addr sym MalSymbol/sym-addr !
    str-len  sym MalSymbol/sym-len !
    -1       sym MalSymbol/map-hint !
    sym ;

: unpack-sym ( mal-string -- addr len )
    dup MalSymbol/sym-addr @
    swap MalSymbol/sym-len @ ;

MalSymbol
  extend mal= ( other this -- bool )
    over mal-type @ MalSymbol = if
        unpack-sym rot unpack-sym str=
    else
        2drop 0
    endif ;;
  extend get-map-hint MalSymbol/map-hint @ ;;
  extend set-map-hint! MalSymbol/map-hint ! ;;
  extend as-native ( this )
    unpack-sym evaluate ;;
drop

MalType%
  cell% field MalKeyword/str-addr
  cell% field MalKeyword/str-len
deftype MalKeyword

: unpack-keyword ( mal-keyword -- addr len )
    dup MalKeyword/str-addr @
    swap MalKeyword/str-len @ ;

MalKeyword
  extend mal= ( other this -- bool )
    over mal-type @ MalKeyword = if
        unpack-keyword rot unpack-keyword str=
    else
        2drop 0
    endif ;;
  as-native-impls ' unpack-keyword extend-method*
drop

: MalKeyword. { str-addr str-len -- mal-keyword }
    MalKeyword new { kw }
    str-addr kw MalKeyword/str-addr !
    str-len  kw MalKeyword/str-len  !
    kw ;

MalType%
  cell% field MalString/str-addr
  cell% field MalString/str-len
deftype MalString

: MalString.0 { str-addr str-len -- mal-str }
    MalString new { str }
    str-addr str MalString/str-addr !
    str-len  str MalString/str-len  !
    str ;
' MalString.0 is MalString.

: unpack-str ( mal-string -- addr len )
    dup MalString/str-addr @
    swap MalString/str-len @ ;

MalString
  extend mal= ( other this -- bool )
    over mal-type @ MalString = if
        unpack-str rot unpack-str str=
    else
        2drop 0
    endif ;;
  as-native-impls ' unpack-str extend-method*
drop


MalType%
  cell% field MalNativeFn/xt
deftype MalNativeFn

: MalNativeFn. { xt -- mal-fn }
    MalNativeFn new { mal-fn }
    xt mal-fn MalNativeFn/xt !
    mal-fn ;


MalType%
  cell% field MalUserFn/is-macro?
  cell% field MalUserFn/env
  cell% field MalUserFn/formal-args
  cell% field MalUserFn/var-arg
  cell% field MalUserFn/body
deftype MalUserFn


MalType%
  cell% field SpecialOp/xt
deftype SpecialOp

: SpecialOp.
    SpecialOp new swap over SpecialOp/xt ! ;

MalType%
  cell% field Atom/val
deftype Atom

: Atom. Atom new swap over Atom/val ! ;
