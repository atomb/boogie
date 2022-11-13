// RUN: %parallel-boogie "%s" > "%t"
// RUN: %diff "%s.expect" "%t"

type{:datatype} Pair;
function{:constructor} Pair(a: int, b: int): Pair;

procedure P0(p: Pair) returns (q: Pair)
  requires p->a == 0;
  ensures  q->a == 1;
{
  q := p;
  q->b := 1;
}

procedure P1(p: [int]Pair, x: int) returns (q: [int]Pair)
  requires p[x]->a == 0;
  ensures  q[x]->a == 1;
{
  q[x] := p[x];
  q[x]->b := 1;
}

type{:datatype} PairOfMaps;
function{:constructor} PairOfMaps(amap: [int]Pair, bmap: [int]Pair): PairOfMaps;

procedure P2(p: PairOfMaps, x: int) returns (q: PairOfMaps)
  requires p->amap[x]->a == 0;
  ensures  q->bmap[x]->a == 0;
{
  var t: [int]Pair;
  q := p;
  call t := P1(p->amap, x);
  q->bmap := t;
}

type{:datatype} Perm;
function{:constructor} Left(i: int): Perm;
function{:constructor} Right(i: int): Perm;

procedure P3(a: int) {
  var left: Perm;
  left->i := a;
  assert left == Left(a);
}

procedure P4(s: Perm) returns (i: int)
ensures i == s->i;
{
  var s': Perm;
  Left(i) := s;
  s' := Left(i);
  assert s == s';
}