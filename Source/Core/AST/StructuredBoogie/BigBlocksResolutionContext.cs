using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using VCGeneration;

namespace Microsoft.Boogie;

class BigBlocksResolutionContext
{
  StmtList /*!*/
    stmtList;

  [Peer] List<Block /*!*/> blocks;

  string /*!*/
    prefix = "anon";

  int anon = 0;

  int FreshAnon()
  {
    return anon++;
  }

  private readonly HashSet<string /*!*/> allLabels = new();

  private readonly Errors /*!*/ errorHandler;

  [ContractInvariantMethod]
  void ObjectInvariant()
  {
    Contract.Invariant(stmtList != null);
    Contract.Invariant(cce.NonNullElements(blocks, true));
    Contract.Invariant(prefix != null);
    Contract.Invariant(cce.NonNullElements(allLabels, true));
    Contract.Invariant(errorHandler != null);
  }

  private void ComputeAllLabels(StmtList stmts)
  {
    if (stmts == null)
    {
      return;
    }

    foreach (BigBlock bb in stmts.BigBlocks)
    {
      if (bb.LabelName != null)
      {
        allLabels.Add(bb.LabelName);
      }

      ComputeAllLabels(bb.ec);
    }
  }

  private void ComputeAllLabels(StructuredCmd cmd)
  {
    if (cmd == null)
    {
      return;
    }

    if (cmd is IfCmd)
    {
      IfCmd ifCmd = (IfCmd) cmd;
      ComputeAllLabels(ifCmd.Thn);
      ComputeAllLabels(ifCmd.ElseIf);
      ComputeAllLabels(ifCmd.ElseBlock);
    }
    else if (cmd is WhileCmd)
    {
      WhileCmd whileCmd = (WhileCmd) cmd;
      ComputeAllLabels(whileCmd.Body);
    }
  }

  public BigBlocksResolutionContext(StmtList stmtList, Errors errorHandler)
  {
    Contract.Requires(errorHandler != null);
    Contract.Requires(stmtList != null);
    this.stmtList = stmtList;
    // Inject an empty big block at the end of the body of a while loop if its current end is another while loop.
    // This transformation creates a suitable jump target for break statements inside the nested while loop.
    InjectEmptyBigBlockInsideWhileLoopBody(stmtList);
    this.errorHandler = errorHandler;
    ComputeAllLabels(stmtList);
  }

  public List<Block /*!*/> /*!*/ Blocks
  {
    get
    {
      Contract.Ensures(cce.NonNullElements(Contract.Result<List<Block>>()));
      if (blocks == null)
      {
        blocks = new List<Block /*!*/>();

        int startErrorCount = this.errorHandler.count;
        // Check that all goto statements go to a label in allLabels, and no break statement to a non-enclosing loop.
        // Also, determine a good value for "prefix".
        CheckLegalLabels(stmtList, null, null);

        // fill in names of anonymous blocks
        NameAnonymousBlocks(stmtList);

        // determine successor blocks
        AssignSuccessors(stmtList, null);

        if (this.errorHandler.count == startErrorCount)
        {
          // generate blocks from the big blocks
          CreateBlocks(stmtList, null);
        }
      }

      return blocks;
    }
  }

  void InjectEmptyBigBlockInsideWhileLoopBody(StmtList stmtList)
  {
    foreach (var bb in stmtList.BigBlocks)
    {
      InjectEmptyBigBlockInsideWhileLoopBody(bb.ec);
    }
  }

  void InjectEmptyBigBlockInsideWhileLoopBody(StructuredCmd structuredCmd)
  {
    if (structuredCmd is WhileCmd whileCmd)
    {
      InjectEmptyBigBlockInsideWhileLoopBody(whileCmd.Body);
      if (whileCmd.Body.BigBlocks.Count > 0 && whileCmd.Body.BigBlocks.Last().ec is WhileCmd)
      {
        var newBigBlocks = new List<BigBlock>(whileCmd.Body.BigBlocks);
        newBigBlocks.Add(new BigBlock(Token.NoToken, null, new List<Cmd>(), null, null));
        whileCmd.Body = new StmtList(newBigBlocks, whileCmd.Body.EndCurly);
      }
    }
    else if (structuredCmd is IfCmd ifCmd)
    {
      InjectEmptyBigBlockInsideWhileLoopBody(ifCmd.Thn);
      if (ifCmd.ElseBlock != null)
      {
        InjectEmptyBigBlockInsideWhileLoopBody(ifCmd.ElseBlock);
      }

      if (ifCmd.ElseIf != null)
      {
        InjectEmptyBigBlockInsideWhileLoopBody(ifCmd.ElseIf);
      }
    }
  }

  void CheckLegalLabels(StmtList stmtList, StmtList parentContext, BigBlock parentBigBlock)
  {
    Contract.Requires(stmtList != null);
    Contract.Requires((parentContext == null) == (parentBigBlock == null));
    Contract.Requires(stmtList.ParentContext == null); // it hasn't been set yet
    //modifies stmtList.*;
    Contract.Ensures(stmtList.ParentContext == parentContext);
    stmtList.ParentContext = parentContext;
    stmtList.ParentBigBlock = parentBigBlock;

    // record the labels declared in this StmtList
    foreach (BigBlock bigBlock in stmtList.BigBlocks)
    {
      if (bigBlock.LabelName != null)
      {
        string n = bigBlock.LabelName;
        if (n.StartsWith(prefix))
        {
          if (prefix.Length < n.Length && n[prefix.Length] == '0')
          {
            prefix += "1";
          }
          else
          {
            prefix += "0";
          }
        }

        stmtList.AddLabel(bigBlock.LabelName);
      }
    }

    // check that labels in this and nested StmtList's are legal
    foreach (var bigBlock in stmtList.BigBlocks)
    {
      // goto's must reference blocks in enclosing blocks
      if (bigBlock.tc is GotoCmd)
      {
        GotoCmd g = (GotoCmd) bigBlock.tc;
        foreach (string /*!*/ lbl in cce.NonNull(g.LabelNames))
        {
          Contract.Assert(lbl != null);
          /*
            bool found = false;
            for (StmtList sl = stmtList; sl != null; sl = sl.ParentContext) {
              if (sl.Labels.Contains(lbl)) {
                found = true;
                break;
              }
            }
            if (!found) {
              this.errorHandler.SemErr(g.tok, "Error: goto label '" + lbl + "' is undefined or out of reach");
            }
            */
          if (!allLabels.Contains(lbl))
          {
            this.errorHandler.SemErr(g.tok, "Error: goto label '" + lbl + "' is undefined");
          }
        }
      }

      // break labels must refer to an enclosing while statement
      else if (bigBlock.ec is BreakCmd)
      {
        BreakCmd bcmd = (BreakCmd) bigBlock.ec;
        Contract.Assert(bcmd.BreakEnclosure == null); // it hasn't been initialized yet
        bool found = false;
        for (StmtList sl = stmtList; sl.ParentBigBlock != null; sl = sl.ParentContext)
        {
          cce.LoopInvariant(sl != null);
          BigBlock bb = sl.ParentBigBlock;

          if (bcmd.Label == null)
          {
            // a label-less break statement breaks out of the innermost enclosing while statement
            if (bb.ec is WhileCmd)
            {
              bcmd.BreakEnclosure = bb;
              found = true;
              break;
            }
          }
          else if (bcmd.Label == bb.LabelName)
          {
            // a break statement with a label can break out of both if statements and while statements
            if (bb.simpleCmds.Count == 0)
            {
              // this is a good target:  the label refers to the if/while statement
              bcmd.BreakEnclosure = bb;
            }
            else
            {
              // the label of bb refers to the first statement of bb, which in which case is a simple statement, not an if/while statement
              this.errorHandler.SemErr(bcmd.tok,
                "Error: break label '" + bcmd.Label + "' must designate an enclosing statement");
            }

            found = true; // don't look any further, since we've found a matching label
            break;
          }
        }

        if (!found)
        {
          if (bcmd.Label == null)
          {
            this.errorHandler.SemErr(bcmd.tok, "Error: break statement is not inside a loop");
          }
          else
          {
            this.errorHandler.SemErr(bcmd.tok,
              "Error: break label '" + bcmd.Label + "' must designate an enclosing statement");
          }
        }
      }

      // recurse
      else if (bigBlock.ec is WhileCmd)
      {
        WhileCmd wcmd = (WhileCmd) bigBlock.ec;
        CheckLegalLabels(wcmd.Body, stmtList, bigBlock);
      }
      else
      {
        for (IfCmd ifcmd = bigBlock.ec as IfCmd; ifcmd != null; ifcmd = ifcmd.ElseIf)
        {
          CheckLegalLabels(ifcmd.Thn, stmtList, bigBlock);
          if (ifcmd.ElseBlock != null)
          {
            CheckLegalLabels(ifcmd.ElseBlock, stmtList, bigBlock);
          }
        }
      }
    }
  }

  void NameAnonymousBlocks(StmtList stmtList)
  {
    Contract.Requires(stmtList != null);
    foreach (var bigBlock in stmtList.BigBlocks)
    {
      if (bigBlock.LabelName == null)
      {
        bigBlock.LabelName = prefix + FreshAnon();
      }

      if (bigBlock.ec is WhileCmd)
      {
        WhileCmd wcmd = (WhileCmd) bigBlock.ec;
        NameAnonymousBlocks(wcmd.Body);
      }
      else
      {
        for (IfCmd ifcmd = bigBlock.ec as IfCmd; ifcmd != null; ifcmd = ifcmd.ElseIf)
        {
          NameAnonymousBlocks(ifcmd.Thn);
          if (ifcmd.ElseBlock != null)
          {
            NameAnonymousBlocks(ifcmd.ElseBlock);
          }
        }
      }
    }
  }

  private static void AssignSuccessors(StmtList stmtList, BigBlock next)
  {
    Contract.Requires(stmtList != null);
    for (int i = stmtList.BigBlocks.Count; 0 <= --i;)
    {
      BigBlock current = stmtList.BigBlocks[i];
      current.SuccessorBigBlock = next;

      if (current.ec is WhileCmd whileCmd)
      {
        AssignSuccessors(whileCmd.Body, current);
      }
      else
      {
        for (IfCmd ifcmd = current.ec as IfCmd; ifcmd != null; ifcmd = ifcmd.ElseIf)
        {
          AssignSuccessors(ifcmd.Thn, next);
          if (ifcmd.ElseBlock != null)
          {
            AssignSuccessors(ifcmd.ElseBlock, next);
          }
        }
      }

      next = current;
    }
  }

  // If the enclosing context is a loop, then "runOffTheEndLabel" is the loop head label;
  // otherwise, it is null.
  void CreateBlocks(StmtList stmtList, string runOffTheEndLabel)
  {
    Contract.Requires(stmtList != null);
    Contract.Requires(blocks != null);
    var cmdPrefixToApply = stmtList.PrefixCommands;

    int remainingBigBlocks = stmtList.BigBlocks.Count;
    foreach (var bigBlock in stmtList.BigBlocks)
    {
      remainingBigBlocks--;
      Contract.Assert(bigBlock.LabelName != null);
      List<Cmd> theSimpleCmds;
      if (cmdPrefixToApply == null)
      {
        theSimpleCmds = bigBlock.simpleCmds;
      }
      else
      {
        theSimpleCmds = new List<Cmd>();
        theSimpleCmds.AddRange(cmdPrefixToApply);
        theSimpleCmds.AddRange(bigBlock.simpleCmds);
        cmdPrefixToApply = null; // now, we've used 'em up
      }

      if (bigBlock.tc != null)
      {
        // this BigBlock has the very same components as a Block
        Contract.Assert(bigBlock.ec == null);
        Block block = new Block(bigBlock.tok, bigBlock.LabelName, theSimpleCmds, bigBlock.tc);
        blocks.Add(block);
      }
      else {
        switch (bigBlock.ec)
        {
          case null:
          {
            TransferCmd trCmd;
            if (remainingBigBlocks == 0 && runOffTheEndLabel != null)
            {
              // goto the given label instead of the textual successor block
              trCmd = new GotoCmd(stmtList.EndCurly, new List<String> {runOffTheEndLabel});
            }
            else
            {
              trCmd = GotoSuccessor(stmtList.EndCurly, bigBlock);
            }

            Block block = new Block(bigBlock.tok, bigBlock.LabelName, theSimpleCmds, trCmd);
            blocks.Add(block);
            break;
          }
          case BreakCmd breakCmd:
          {
            BreakCmd bcmd = breakCmd;
            Contract.Assert(bcmd.BreakEnclosure != null);
            Block block = new Block(bigBlock.tok, bigBlock.LabelName, theSimpleCmds, GotoSuccessor(breakCmd.tok, bcmd.BreakEnclosure));
            blocks.Add(block);
            break;
          }
          case WhileCmd whileCmd:
          {
            WhileCmd wcmd = whileCmd;
            var a = FreshAnon();
            string loopHeadLabel = prefix + a + "_LoopHead";
            string /*!*/
              loopBodyLabel = prefix + a + "_LoopBody";
            string loopDoneLabel = prefix + a + "_LoopDone";

            List<Cmd> ssBody = new List<Cmd>();
            List<Cmd> ssDone = new List<Cmd>();
            if (wcmd.Guard != null)
            {
              var ac = new AssumeCmd(wcmd.tok, wcmd.Guard);
              ac.Attributes = new QKeyValue(wcmd.tok, "partition", new List<object>(), null);
              ssBody.Add(ac);

              ac = new AssumeCmd(wcmd.tok, Expr.Not(wcmd.Guard));
              ac.Attributes = new QKeyValue(wcmd.tok, "partition", new List<object>(), null);
              ssDone.Add(ac);
            }

            // Try to squeeze in ssBody into the first block of wcmd.Body
            bool bodyGuardTakenCareOf = wcmd.Body.PrefixFirstBlock(ssBody, ref loopBodyLabel);

            // ... goto LoopHead;
            Block block = new Block(bigBlock.tok, bigBlock.LabelName, theSimpleCmds,
              new GotoCmd(wcmd.tok, new List<String> {loopHeadLabel}));
            blocks.Add(block);

            // LoopHead: assert/assume loop_invariant; goto LoopDone, LoopBody;
            List<Cmd> ssHead = new List<Cmd>();
            foreach (CallCmd yield in wcmd.Yields)
            {
              ssHead.Add(yield);
            }
            foreach (PredicateCmd inv in wcmd.Invariants)
            {
              ssHead.Add(inv);
            }

            block = new Block(wcmd.tok, loopHeadLabel, ssHead,
              new GotoCmd(wcmd.tok, new List<String> {loopDoneLabel, loopBodyLabel}));
            blocks.Add(block);

            if (!bodyGuardTakenCareOf)
            {
              // LoopBody: assume guard; goto firstLoopBlock;
              block = new Block(wcmd.tok, loopBodyLabel, ssBody,
                new GotoCmd(wcmd.tok, new List<String> {wcmd.Body.BigBlocks[0].LabelName}));
              blocks.Add(block);
            }

            // recurse to create the blocks for the loop body
            CreateBlocks(wcmd.Body, loopHeadLabel);

            // LoopDone: assume !guard; goto loopSuccessor;
            TransferCmd trCmd;
            if (remainingBigBlocks == 0 && runOffTheEndLabel != null)
            {
              // goto the given label instead of the textual successor block
              trCmd = new GotoCmd(wcmd.tok, new List<String> {runOffTheEndLabel});
            }
            else
            {
              trCmd = GotoSuccessor(wcmd.tok, bigBlock);
            }

            block = new Block(wcmd.tok, loopDoneLabel, ssDone, trCmd);
            blocks.Add(block);
            break;
          }
          default:
          {
            IfCmd ifcmd = (IfCmd) bigBlock.ec;
            string predLabel = bigBlock.LabelName;
            List<Cmd> predCmds = theSimpleCmds;

            for (; ifcmd != null; ifcmd = ifcmd.ElseIf)
            {
              var a = FreshAnon();
              string thenLabel = prefix + a + "_Then";
              Contract.Assert(thenLabel != null);
              string elseLabel = prefix + a + "_Else";
              Contract.Assert(elseLabel != null);

              List<Cmd> ssThen = new List<Cmd>();
              List<Cmd> ssElse = new List<Cmd>();
              if (ifcmd.Guard != null)
              {
                var ac = new AssumeCmd(ifcmd.tok, ifcmd.Guard);
                ac.Attributes = new QKeyValue(ifcmd.tok, "partition", new List<object>(), null);
                ssThen.Add(ac);

                ac = new AssumeCmd(ifcmd.tok, Expr.Not(ifcmd.Guard));
                ac.Attributes = new QKeyValue(ifcmd.tok, "partition", new List<object>(), null);
                ssElse.Add(ac);
              }

              // Try to squeeze in ssThen/ssElse into the first block of ifcmd.thn/ifcmd.elseBlock
              bool thenGuardTakenCareOf = ifcmd.Thn.PrefixFirstBlock(ssThen, ref thenLabel);
              bool elseGuardTakenCareOf = false;
              if (ifcmd.ElseBlock != null)
              {
                elseGuardTakenCareOf = ifcmd.ElseBlock.PrefixFirstBlock(ssElse, ref elseLabel);
              }

              // ... goto Then, Else;
              var jumpBlock = new Block(bigBlock.tok, predLabel, predCmds,
                new GotoCmd(ifcmd.tok, new List<String> {thenLabel, elseLabel}));
              blocks.Add(jumpBlock);

              if (!thenGuardTakenCareOf)
              {
                // Then: assume guard; goto firstThenBlock;
                var thenJumpBlock = new Block(ifcmd.tok, thenLabel, ssThen,
                  new GotoCmd(ifcmd.tok, new List<String> {ifcmd.Thn.BigBlocks[0].LabelName}));
                blocks.Add(thenJumpBlock);
              }

              // recurse to create the blocks for the then branch
              CreateBlocks(ifcmd.Thn, remainingBigBlocks == 0 ? runOffTheEndLabel : null);

              if (ifcmd.ElseBlock != null)
              {
                Contract.Assert(ifcmd.ElseIf == null);
                if (!elseGuardTakenCareOf)
                {
                  // Else: assume !guard; goto firstElseBlock;
                  var elseJumpBlock = new Block(ifcmd.tok, elseLabel, ssElse,
                    new GotoCmd(ifcmd.tok, new List<String> {ifcmd.ElseBlock.BigBlocks[0].LabelName}));
                  blocks.Add(elseJumpBlock);
                }

                // recurse to create the blocks for the else branch
                CreateBlocks(ifcmd.ElseBlock, remainingBigBlocks == 0 ? runOffTheEndLabel : null);
              }
              else if (ifcmd.ElseIf != null)
              {
                // this is an "else if"
                predLabel = elseLabel;
                predCmds = new List<Cmd>();
                if (ifcmd.Guard != null)
                {
                  var ac = new AssumeCmd(ifcmd.tok, Expr.Not(ifcmd.Guard));
                  ac.Attributes = new QKeyValue(ifcmd.tok, "partition", new List<object>(), null);
                  predCmds.Add(ac);
                }
              }
              else
              {
                // no else alternative is specified, so else branch is just "skip"
                // Else: assume !guard; goto ifSuccessor;
                TransferCmd trCmd;
                if (remainingBigBlocks == 0 && runOffTheEndLabel != null)
                {
                  // goto the given label instead of the textual successor block
                  trCmd = new GotoCmd(ifcmd.tok, new List<String> {runOffTheEndLabel});
                }
                else
                {
                  trCmd = GotoSuccessor(ifcmd.tok, bigBlock);
                }

                var block = new Block(ifcmd.tok, elseLabel, ssElse, trCmd);
                blocks.Add(block);
              }
            }

            break;
          }
        }
      }
    }
  }

  TransferCmd GotoSuccessor(IToken tok, BigBlock b)
  {
    Contract.Requires(b != null);
    Contract.Requires(tok != null);
    Contract.Ensures(Contract.Result<TransferCmd>() != null);
    if (b.SuccessorBigBlock != null)
    {
      return new GotoCmd(new ImplicitJump(tok), new List<string> {b.SuccessorBigBlock.LabelName});
    }
    else
    {
      return new ReturnCmd(new ImplicitJump(tok));
    }
  }
}