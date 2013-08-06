using System.Collections.Generic;
using System.Numerics;
using Microsoft.Boogie;
using System.IO;
using System.Text;


using System;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny {



public class Parser {
	public const int _EOF = 0;
	public const int _ident = 1;
	public const int _digits = 2;
	public const int _hexdigits = 3;
	public const int _arrayToken = 4;
	public const int _string = 5;
	public const int _colon = 6;
	public const int _semi = 7;
	public const int _lbrace = 8;
	public const int _rbrace = 9;
	public const int _openparen = 10;
	public const int _star = 11;
	public const int _notIn = 12;
	public const int maxT = 122;

	const bool T = true;
	const bool x = false;
	const int minErrDist = 2;

	public Scanner/*!*/ scanner;
	public Errors/*!*/  errors;

	public Token/*!*/ t;    // last recognized token
	public Token/*!*/ la;   // lookahead token
	int errDist = minErrDist;

readonly Expression/*!*/ dummyExpr;
readonly AssignmentRhs/*!*/ dummyRhs;
readonly FrameExpression/*!*/ dummyFrameExpr;
readonly Statement/*!*/ dummyStmt;
readonly Attributes.Argument/*!*/ dummyAttrArg;
readonly ModuleDecl theModule;
readonly BuiltIns theBuiltIns;
int anonymousIds = 0;

struct MemberModifiers {
  public bool IsGhost;
  public bool IsStatic;
}

///<summary>
/// Parses top-level things (modules, classes, datatypes, class members) from "filename"
/// and appends them in appropriate form to "module".
/// Returns the number of parsing errors encountered.
/// Note: first initialize the Scanner.
///</summary>
public static int Parse (string/*!*/ filename, ModuleDecl module, BuiltIns builtIns) /* throws System.IO.IOException */ {
  Contract.Requires(filename != null);
  Contract.Requires(module != null);
  string s;
  if (filename == "stdin.dfy") {
    s = Microsoft.Boogie.ParserHelper.Fill(System.Console.In, new List<string>());
    return Parse(s, filename, module, builtIns);
  } else {
    using (System.IO.StreamReader reader = new System.IO.StreamReader(filename)) {
      s = Microsoft.Boogie.ParserHelper.Fill(reader, new List<string>());
      return Parse(s, filename, module, builtIns);
    }
  }
}
///<summary>
/// Parses top-level things (modules, classes, datatypes, class members)
/// and appends them in appropriate form to "module".
/// Returns the number of parsing errors encountered.
/// Note: first initialize the Scanner.
///</summary>
public static int Parse (string/*!*/ s, string/*!*/ filename, ModuleDecl module, BuiltIns builtIns) {
  Contract.Requires(s != null);
  Contract.Requires(filename != null);
  Contract.Requires(module != null);
  Errors errors = new Errors();
  return Parse(s, filename, module, builtIns, errors);
}
///<summary>
/// Parses top-level things (modules, classes, datatypes, class members)
/// and appends them in appropriate form to "module".
/// Returns the number of parsing errors encountered.
/// Note: first initialize the Scanner with the given Errors sink.
///</summary>
public static int Parse (string/*!*/ s, string/*!*/ filename, ModuleDecl module, BuiltIns builtIns,
                         Errors/*!*/ errors) {
  Contract.Requires(s != null);
  Contract.Requires(filename != null);
  Contract.Requires(module != null);
  Contract.Requires(errors != null);
  byte[]/*!*/ buffer = cce.NonNull( UTF8Encoding.Default.GetBytes(s));
  MemoryStream ms = new MemoryStream(buffer,false);
  Scanner scanner = new Scanner(ms, errors, filename);
  Parser parser = new Parser(scanner, errors, module, builtIns);
  parser.Parse();
  return parser.errors.count;
}
public Parser(Scanner/*!*/ scanner, Errors/*!*/ errors, ModuleDecl module, BuiltIns builtIns) 
  : this(scanner, errors)  // the real work
{
  // initialize readonly fields
  dummyExpr = new LiteralExpr(Token.NoToken);
  dummyRhs = new ExprRhs(dummyExpr, null);
  dummyFrameExpr = new FrameExpression(dummyExpr.tok, dummyExpr, null);
  dummyStmt = new ReturnStmt(Token.NoToken, null);
  dummyAttrArg = new Attributes.Argument(Token.NoToken, "dummyAttrArg");
  theModule = module;
  theBuiltIns = builtIns;
}

bool IsAttribute() {
  Token x = scanner.Peek();
  return la.kind == _lbrace && x.kind == _colon;
}

bool IsAlternative() {
  Token x = scanner.Peek();
  return la.kind == _lbrace && x.val == "case";
}

bool IsLoopSpec() {
  return la.val == "invariant" | la.val == "decreases" | la.val == "modifies";
}

bool IsLoopSpecOrAlternative() {
  Token x = scanner.Peek();
  return IsLoopSpec() || (la.kind == _lbrace && x.val == "case");
}

bool IsParenStar() {
  scanner.ResetPeek();
  Token x = scanner.Peek();
  return la.kind == _openparen && x.kind == _star;
}

bool SemiFollowsCall(Expression e) {
  return la.kind == _semi &&
    (e is FunctionCallExpr ||
     (e is IdentifierSequence && ((IdentifierSequence)e).OpenParen != null));
}
/*--------------------------------------------------------------------------*/


	public Parser(Scanner/*!*/ scanner, Errors/*!*/ errors) {
		this.scanner = scanner;
		this.errors = errors;
		Token/*!*/ tok = new Token();
		tok.val = "";
		this.la = tok;
		this.t = new Token(); // just to satisfy its non-null constraint
	}

	void SynErr (int n) {
		if (errDist >= minErrDist) errors.SynErr(la.filename, la.line, la.col, n);
		errDist = 0;
	}

	public void SemErr (string/*!*/ msg) {
		Contract.Requires(msg != null);
		if (errDist >= minErrDist) errors.SemErr(t, msg);
		errDist = 0;
	}

	public void SemErr(IToken/*!*/ tok, string/*!*/ msg) {
	  Contract.Requires(tok != null);
	  Contract.Requires(msg != null);
	  errors.SemErr(tok, msg);
	}

	void Get () {
		for (;;) {
			t = la;
			la = scanner.Scan();
			if (la.kind <= maxT) { ++errDist; break; }

			la = t;
		}
	}

	void Expect (int n) {
		if (la.kind==n) Get(); else { SynErr(n); }
	}

	bool StartOf (int s) {
		return set[s, la.kind];
	}

	void ExpectWeak (int n, int follow) {
		if (la.kind == n) Get();
		else {
			SynErr(n);
			while (!StartOf(follow)) Get();
		}
	}


	bool WeakSeparator(int n, int syFol, int repFol) {
		int kind = la.kind;
		if (kind == n) {Get(); return true;}
		else if (StartOf(repFol)) {return false;}
		else {
			SynErr(n);
			while (!(set[syFol, kind] || set[repFol, kind] || set[0, kind])) {
				Get();
				kind = la.kind;
			}
			return StartOf(syFol);
		}
	}


	void Dafny() {
		ClassDecl/*!*/ c; DatatypeDecl/*!*/ dt; ArbitraryTypeDecl at; IteratorDecl iter;
		List<MemberDecl/*!*/> membersDefaultClass = new List<MemberDecl/*!*/>();
		ModuleDecl submodule;
		// to support multiple files, create a default module only if theModule is null
		DefaultModuleDecl defaultModule = (DefaultModuleDecl)((LiteralModuleDecl)theModule).ModuleDef;
		// theModule should be a DefaultModuleDecl (actually, the singular DefaultModuleDecl)
		Contract.Assert(defaultModule != null);
		
		while (StartOf(1)) {
			switch (la.kind) {
			case 13: case 14: case 16: {
				SubModuleDecl(defaultModule, out submodule);
				defaultModule.TopLevelDecls.Add(submodule); 
				break;
			}
			case 22: {
				ClassDecl(defaultModule, out c);
				defaultModule.TopLevelDecls.Add(c); 
				break;
			}
			case 25: case 26: {
				DatatypeDecl(defaultModule, out dt);
				defaultModule.TopLevelDecls.Add(dt); 
				break;
			}
			case 30: {
				ArbitraryTypeDecl(defaultModule, out at);
				defaultModule.TopLevelDecls.Add(at); 
				break;
			}
			case 33: {
				IteratorDecl(defaultModule, out iter);
				defaultModule.TopLevelDecls.Add(iter); 
				break;
			}
			case 23: case 24: case 28: case 39: case 40: case 41: case 42: case 43: case 59: case 60: case 61: {
				ClassMemberDecl(membersDefaultClass, false);
				break;
			}
			}
		}
		DefaultClassDecl defaultClass = null;
		foreach (TopLevelDecl topleveldecl in defaultModule.TopLevelDecls) {
		 defaultClass = topleveldecl as DefaultClassDecl;
		 if (defaultClass != null) {
		   defaultClass.Members.AddRange(membersDefaultClass);
		   break;
		 }
		}
		if (defaultClass == null) { // create the default class here, because it wasn't found
		 defaultClass = new DefaultClassDecl(defaultModule, membersDefaultClass);
		 defaultModule.TopLevelDecls.Add(defaultClass);
		} 
		Expect(0);
	}

	void SubModuleDecl(ModuleDefinition parent, out ModuleDecl submodule) {
		ClassDecl/*!*/ c; DatatypeDecl/*!*/ dt; ArbitraryTypeDecl at; IteratorDecl iter;
		Attributes attrs = null;  IToken/*!*/ id; 
		List<MemberDecl/*!*/> namedModuleDefaultClassMembers = new List<MemberDecl>();;
		List<IToken> idRefined = null, idPath = null, idAssignment = null;
		ModuleDefinition module;
		ModuleDecl sm;
		submodule = null; // appease compiler
		bool isAbstract = false;
		bool opened = false;
		
		if (la.kind == 13 || la.kind == 14) {
			if (la.kind == 13) {
				Get();
				isAbstract = true; 
			}
			Expect(14);
			while (la.kind == 8) {
				Attribute(ref attrs);
			}
			NoUSIdent(out id);
			if (la.kind == 15) {
				Get();
				QualifiedName(out idRefined);
			}
			module = new ModuleDefinition(id, id.val, isAbstract, false, idRefined == null ? null : idRefined, attrs, false); 
			Expect(8);
			module.BodyStartTok = t; 
			while (StartOf(1)) {
				switch (la.kind) {
				case 13: case 14: case 16: {
					SubModuleDecl(module, out sm);
					module.TopLevelDecls.Add(sm); 
					break;
				}
				case 22: {
					ClassDecl(module, out c);
					module.TopLevelDecls.Add(c); 
					break;
				}
				case 25: case 26: {
					DatatypeDecl(module, out dt);
					module.TopLevelDecls.Add(dt); 
					break;
				}
				case 30: {
					ArbitraryTypeDecl(module, out at);
					module.TopLevelDecls.Add(at); 
					break;
				}
				case 33: {
					IteratorDecl(module, out iter);
					module.TopLevelDecls.Add(iter); 
					break;
				}
				case 23: case 24: case 28: case 39: case 40: case 41: case 42: case 43: case 59: case 60: case 61: {
					ClassMemberDecl(namedModuleDefaultClassMembers, false);
					break;
				}
				}
			}
			Expect(9);
			module.BodyEndTok = t;
			module.TopLevelDecls.Add(new DefaultClassDecl(module, namedModuleDefaultClassMembers));
			submodule = new LiteralModuleDecl(module, parent); 
		} else if (la.kind == 16) {
			Get();
			if (la.kind == 17) {
				Get();
				opened = true;
			}
			NoUSIdent(out id);
			if (la.kind == 18 || la.kind == 19) {
				if (la.kind == 18) {
					Get();
					QualifiedName(out idPath);
					submodule = new AliasModuleDecl(idPath, id, parent, opened); 
				} else {
					Get();
					QualifiedName(out idPath);
					if (la.kind == 20) {
						Get();
						QualifiedName(out idAssignment);
					}
					submodule = new ModuleFacadeDecl(idPath, id, parent, idAssignment, opened); 
				}
			}
			if (la.kind == 7) {
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(123); Get();}
				Get();
			}
			if (submodule == null) {
			 idPath = new List<IToken>();
			 idPath.Add(id);
			 submodule = new AliasModuleDecl(idPath, id, parent, opened);
			}
			
		} else SynErr(124);
	}

	void ClassDecl(ModuleDefinition/*!*/ module, out ClassDecl/*!*/ c) {
		Contract.Requires(module != null);
		Contract.Ensures(Contract.ValueAtReturn(out c) != null);
		IToken/*!*/ id;
		Attributes attrs = null;
		List<TypeParameter/*!*/> typeArgs = new List<TypeParameter/*!*/>();
		List<MemberDecl/*!*/> members = new List<MemberDecl/*!*/>();
		IToken bodyStart;
		
		while (!(la.kind == 0 || la.kind == 22)) {SynErr(125); Get();}
		Expect(22);
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		NoUSIdent(out id);
		if (la.kind == 37) {
			GenericParameters(typeArgs);
		}
		Expect(8);
		bodyStart = t; 
		while (StartOf(2)) {
			ClassMemberDecl(members, true);
		}
		Expect(9);
		c = new ClassDecl(id, id.val, module, typeArgs, members, attrs);
		c.BodyStartTok = bodyStart;
		c.BodyEndTok = t;
		
	}

	void DatatypeDecl(ModuleDefinition/*!*/ module, out DatatypeDecl/*!*/ dt) {
		Contract.Requires(module != null);
		Contract.Ensures(Contract.ValueAtReturn(out dt)!=null);
		IToken/*!*/ id;
		Attributes attrs = null;
		List<TypeParameter/*!*/> typeArgs = new List<TypeParameter/*!*/>();
		List<DatatypeCtor/*!*/> ctors = new List<DatatypeCtor/*!*/>();
		IToken bodyStart = Token.NoToken;  // dummy assignment
		bool co = false;
		
		while (!(la.kind == 0 || la.kind == 25 || la.kind == 26)) {SynErr(126); Get();}
		if (la.kind == 25) {
			Get();
		} else if (la.kind == 26) {
			Get();
			co = true; 
		} else SynErr(127);
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		NoUSIdent(out id);
		if (la.kind == 37) {
			GenericParameters(typeArgs);
		}
		Expect(18);
		bodyStart = t; 
		DatatypeMemberDecl(ctors);
		while (la.kind == 27) {
			Get();
			DatatypeMemberDecl(ctors);
		}
		if (la.kind == 7) {
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(128); Get();}
			Get();
		}
		if (co) {
		 dt = new CoDatatypeDecl(id, id.val, module, typeArgs, ctors, attrs);
		} else {
		 dt = new IndDatatypeDecl(id, id.val, module, typeArgs, ctors, attrs);
		}
		dt.BodyStartTok = bodyStart;
		dt.BodyEndTok = t;
		
	}

	void ArbitraryTypeDecl(ModuleDefinition/*!*/ module, out ArbitraryTypeDecl at) {
		IToken/*!*/ id;
		Attributes attrs = null;
		var eqSupport = TypeParameter.EqualitySupportValue.Unspecified;
		
		Expect(30);
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		NoUSIdent(out id);
		if (la.kind == 10) {
			Get();
			Expect(31);
			Expect(32);
			eqSupport = TypeParameter.EqualitySupportValue.Required; 
		}
		at = new ArbitraryTypeDecl(id, id.val, module, eqSupport, attrs); 
		if (la.kind == 7) {
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(129); Get();}
			Get();
		}
	}

	void IteratorDecl(ModuleDefinition module, out IteratorDecl/*!*/ iter) {
		Contract.Ensures(Contract.ValueAtReturn(out iter) != null);
		IToken/*!*/ id;
		Attributes attrs = null;
		List<TypeParameter/*!*/>/*!*/ typeArgs = new List<TypeParameter/*!*/>();
		IToken openParen;
		List<Formal/*!*/> ins = new List<Formal/*!*/>();
		List<Formal/*!*/> outs = new List<Formal/*!*/>();
		List<FrameExpression/*!*/> reads = new List<FrameExpression/*!*/>();
		List<FrameExpression/*!*/> mod = new List<FrameExpression/*!*/>();
		List<Expression/*!*/> decreases = new List<Expression>();
		List<MaybeFreeExpression/*!*/> req = new List<MaybeFreeExpression/*!*/>();
		List<MaybeFreeExpression/*!*/> ens = new List<MaybeFreeExpression/*!*/>();
		List<MaybeFreeExpression/*!*/> yieldReq = new List<MaybeFreeExpression/*!*/>();
		List<MaybeFreeExpression/*!*/> yieldEns = new List<MaybeFreeExpression/*!*/>();
		List<Expression/*!*/> dec = new List<Expression/*!*/>();
		Attributes readsAttrs = null;
		Attributes modAttrs = null;
		Attributes decrAttrs = null;
		BlockStmt body = null;
		bool signatureOmitted = false;
		IToken bodyStart = Token.NoToken;
		IToken bodyEnd = Token.NoToken;
		
		while (!(la.kind == 0 || la.kind == 33)) {SynErr(130); Get();}
		Expect(33);
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		NoUSIdent(out id);
		if (la.kind == 10 || la.kind == 37) {
			if (la.kind == 37) {
				GenericParameters(typeArgs);
			}
			Formals(true, true, ins, out openParen);
			if (la.kind == 34 || la.kind == 35) {
				if (la.kind == 34) {
					Get();
				} else {
					Get();
					SemErr(t, "iterators don't have a 'returns' clause; did you mean 'yields'?"); 
				}
				Formals(false, true, outs, out openParen);
			}
		} else if (la.kind == 36) {
			Get();
			signatureOmitted = true; openParen = Token.NoToken; 
		} else SynErr(131);
		while (StartOf(3)) {
			IteratorSpec(reads, mod, decreases, req, ens, yieldReq, yieldEns, ref readsAttrs, ref modAttrs, ref decrAttrs);
		}
		if (la.kind == 8) {
			BlockStmt(out body, out bodyStart, out bodyEnd);
		}
		iter = new IteratorDecl(id, id.val, module, typeArgs, ins, outs,
		                       new Specification<FrameExpression>(reads, readsAttrs),
		                       new Specification<FrameExpression>(mod, modAttrs),
		                       new Specification<Expression>(decreases, decrAttrs),
		                       req, ens, yieldReq, yieldEns,
		                       body, attrs, signatureOmitted);
		iter.BodyStartTok = bodyStart;
		iter.BodyEndTok = bodyEnd;
		
	}

	void ClassMemberDecl(List<MemberDecl/*!*/>/*!*/ mm, bool allowConstructors) {
		Contract.Requires(cce.NonNullElements(mm));
		Method/*!*/ m;
		Function/*!*/ f;
		MemberModifiers mmod = new MemberModifiers();
		
		while (la.kind == 23 || la.kind == 24) {
			if (la.kind == 23) {
				Get();
				mmod.IsGhost = true; 
			} else {
				Get();
				mmod.IsStatic = true; 
			}
		}
		if (la.kind == 28) {
			FieldDecl(mmod, mm);
		} else if (la.kind == 59 || la.kind == 60 || la.kind == 61) {
			FunctionDecl(mmod, out f);
			mm.Add(f); 
		} else if (StartOf(4)) {
			MethodDecl(mmod, allowConstructors, out m);
			mm.Add(m); 
		} else SynErr(132);
	}

	void Attribute(ref Attributes attrs) {
		Expect(8);
		AttributeBody(ref attrs);
		Expect(9);
	}

	void NoUSIdent(out IToken/*!*/ x) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); 
		Expect(1);
		x = t; 
		if (x.val.StartsWith("_")) {
		 SemErr("cannot declare identifier beginning with underscore");
		}
		
	}

	void QualifiedName(out List<IToken> ids) {
		IToken id; ids = new List<IToken>(); 
		Ident(out id);
		ids.Add(id); 
		while (la.kind == 21) {
			Get();
			IdentOrDigits(out id);
			ids.Add(id); 
		}
	}

	void Ident(out IToken/*!*/ x) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); 
		Expect(1);
		x = t; 
	}

	void IdentOrDigits(out IToken/*!*/ x) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); x = Token.NoToken; 
		if (la.kind == 1) {
			Get();
			x = t; 
		} else if (la.kind == 2) {
			Get();
			x = t; 
		} else SynErr(133);
	}

	void GenericParameters(List<TypeParameter/*!*/>/*!*/ typeArgs) {
		Contract.Requires(cce.NonNullElements(typeArgs));
		IToken/*!*/ id;
		TypeParameter.EqualitySupportValue eqSupport;
		
		Expect(37);
		NoUSIdent(out id);
		eqSupport = TypeParameter.EqualitySupportValue.Unspecified; 
		if (la.kind == 10) {
			Get();
			Expect(31);
			Expect(32);
			eqSupport = TypeParameter.EqualitySupportValue.Required; 
		}
		typeArgs.Add(new TypeParameter(id, id.val, eqSupport)); 
		while (la.kind == 29) {
			Get();
			NoUSIdent(out id);
			eqSupport = TypeParameter.EqualitySupportValue.Unspecified; 
			if (la.kind == 10) {
				Get();
				Expect(31);
				Expect(32);
				eqSupport = TypeParameter.EqualitySupportValue.Required; 
			}
			typeArgs.Add(new TypeParameter(id, id.val, eqSupport)); 
		}
		Expect(38);
	}

	void FieldDecl(MemberModifiers mmod, List<MemberDecl/*!*/>/*!*/ mm) {
		Contract.Requires(cce.NonNullElements(mm));
		Attributes attrs = null;
		IToken/*!*/ id;  Type/*!*/ ty;
		
		while (!(la.kind == 0 || la.kind == 28)) {SynErr(134); Get();}
		Expect(28);
		if (mmod.IsStatic) { SemErr(t, "fields cannot be declared 'static'"); }
		
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		FIdentType(out id, out ty);
		mm.Add(new Field(id, id.val, mmod.IsGhost, ty, attrs)); 
		while (la.kind == 29) {
			Get();
			FIdentType(out id, out ty);
			mm.Add(new Field(id, id.val, mmod.IsGhost, ty, attrs)); 
		}
		while (!(la.kind == 0 || la.kind == 7)) {SynErr(135); Get();}
		Expect(7);
	}

	void FunctionDecl(MemberModifiers mmod, out Function/*!*/ f) {
		Contract.Ensures(Contract.ValueAtReturn(out f)!=null);
		Attributes attrs = null;
		IToken/*!*/ id = Token.NoToken;  // to please compiler
		List<TypeParameter/*!*/> typeArgs = new List<TypeParameter/*!*/>();
		List<Formal/*!*/> formals = new List<Formal/*!*/>();
		Type/*!*/ returnType = new BoolType();
		List<Expression/*!*/> reqs = new List<Expression/*!*/>();
		List<Expression/*!*/> ens = new List<Expression/*!*/>();
		List<FrameExpression/*!*/> reads = new List<FrameExpression/*!*/>();
		List<Expression/*!*/> decreases;
		Expression body = null;
		bool isPredicate = false;  bool isCoPredicate = false;
		bool isFunctionMethod = false;
		IToken openParen = null;
		IToken bodyStart = Token.NoToken;
		IToken bodyEnd = Token.NoToken;
		bool signatureOmitted = false;
		
		if (la.kind == 59) {
			Get();
			if (la.kind == 39) {
				Get();
				isFunctionMethod = true; 
			}
			if (mmod.IsGhost) { SemErr(t, "functions cannot be declared 'ghost' (they are ghost by default)"); }
			
			while (la.kind == 8) {
				Attribute(ref attrs);
			}
			NoUSIdent(out id);
			if (la.kind == 10 || la.kind == 37) {
				if (la.kind == 37) {
					GenericParameters(typeArgs);
				}
				Formals(true, isFunctionMethod, formals, out openParen);
				Expect(6);
				Type(out returnType);
			} else if (la.kind == 36) {
				Get();
				signatureOmitted = true;
				openParen = Token.NoToken; 
			} else SynErr(136);
		} else if (la.kind == 60) {
			Get();
			isPredicate = true; 
			if (la.kind == 39) {
				Get();
				isFunctionMethod = true; 
			}
			if (mmod.IsGhost) { SemErr(t, "predicates cannot be declared 'ghost' (they are ghost by default)"); }
			
			while (la.kind == 8) {
				Attribute(ref attrs);
			}
			NoUSIdent(out id);
			if (StartOf(5)) {
				if (la.kind == 37) {
					GenericParameters(typeArgs);
				}
				if (la.kind == 10) {
					Formals(true, isFunctionMethod, formals, out openParen);
					if (la.kind == 6) {
						Get();
						SemErr(t, "predicates do not have an explicitly declared return type; it is always bool"); 
					}
				}
			} else if (la.kind == 36) {
				Get();
				signatureOmitted = true;
				openParen = Token.NoToken; 
			} else SynErr(137);
		} else if (la.kind == 61) {
			Get();
			isCoPredicate = true; 
			if (mmod.IsGhost) { SemErr(t, "copredicates cannot be declared 'ghost' (they are ghost by default)"); }
			
			while (la.kind == 8) {
				Attribute(ref attrs);
			}
			NoUSIdent(out id);
			if (StartOf(5)) {
				if (la.kind == 37) {
					GenericParameters(typeArgs);
				}
				if (la.kind == 10) {
					Formals(true, isFunctionMethod, formals, out openParen);
					if (la.kind == 6) {
						Get();
						SemErr(t, "copredicates do not have an explicitly declared return type; it is always bool"); 
					}
				}
			} else if (la.kind == 36) {
				Get();
				signatureOmitted = true;
				openParen = Token.NoToken; 
			} else SynErr(138);
		} else SynErr(139);
		decreases = isCoPredicate ? null : new List<Expression/*!*/>(); 
		while (StartOf(6)) {
			FunctionSpec(reqs, reads, ens, decreases);
		}
		if (la.kind == 8) {
			FunctionBody(out body, out bodyStart, out bodyEnd);
		}
		if (isPredicate) {
		  f = new Predicate(id, id.val, mmod.IsStatic, !isFunctionMethod, typeArgs, openParen, formals,
		                    reqs, reads, ens, new Specification<Expression>(decreases, null), body, Predicate.BodyOriginKind.OriginalOrInherited, attrs, signatureOmitted);
		} else if (isCoPredicate) {
		  f = new CoPredicate(id, id.val, mmod.IsStatic, typeArgs, openParen, formals,
		                    reqs, reads, ens, body, attrs, signatureOmitted);
		} else {
		  f = new Function(id, id.val, mmod.IsStatic, !isFunctionMethod, typeArgs, openParen, formals, returnType,
		                   reqs, reads, ens, new Specification<Expression>(decreases, null), body, attrs, signatureOmitted);
		}
		f.BodyStartTok = bodyStart;
		f.BodyEndTok = bodyEnd;
		
	}

	void MethodDecl(MemberModifiers mmod, bool allowConstructor, out Method/*!*/ m) {
		Contract.Ensures(Contract.ValueAtReturn(out m) !=null);
		IToken/*!*/ id = Token.NoToken;
		bool hasName = false;  IToken keywordToken;
		Attributes attrs = null;
		List<TypeParameter/*!*/>/*!*/ typeArgs = new List<TypeParameter/*!*/>();
		IToken openParen;
		List<Formal/*!*/> ins = new List<Formal/*!*/>();
		List<Formal/*!*/> outs = new List<Formal/*!*/>();
		List<MaybeFreeExpression/*!*/> req = new List<MaybeFreeExpression/*!*/>();
		List<FrameExpression/*!*/> mod = new List<FrameExpression/*!*/>();
		List<MaybeFreeExpression/*!*/> ens = new List<MaybeFreeExpression/*!*/>();
		List<Expression/*!*/> dec = new List<Expression/*!*/>();
		Attributes decAttrs = null;
		Attributes modAttrs = null;
		BlockStmt body = null;
		bool isLemma = false;
		bool isConstructor = false;
		bool isCoMethod = false;
		bool signatureOmitted = false;
		IToken bodyStart = Token.NoToken;
		IToken bodyEnd = Token.NoToken;
		
		while (!(StartOf(7))) {SynErr(140); Get();}
		if (la.kind == 39) {
			Get();
		} else if (la.kind == 40) {
			Get();
			isLemma = true; 
		} else if (la.kind == 41) {
			Get();
			isCoMethod = true; 
		} else if (la.kind == 42) {
			Get();
			isCoMethod = true; 
		} else if (la.kind == 43) {
			Get();
			if (allowConstructor) {
			 isConstructor = true;
			} else {
			 SemErr(t, "constructors are only allowed in classes");
			}
			
		} else SynErr(141);
		keywordToken = t; 
		if (isLemma) {
		 if (mmod.IsGhost) {
		   SemErr(t, "lemmas cannot be declared 'ghost' (they are automatically 'ghost')");
		 }
		} else if (isConstructor) {
		 if (mmod.IsGhost) {
		   SemErr(t, "constructors cannot be declared 'ghost'");
		 }
		 if (mmod.IsStatic) {
		   SemErr(t, "constructors cannot be declared 'static'");
		 }
		} else if (isCoMethod) {
		 if (mmod.IsGhost) {
		   SemErr(t, "comethods cannot be declared 'ghost' (they are automatically 'ghost')");
		 }
		}
		
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		if (la.kind == 1) {
			NoUSIdent(out id);
			hasName = true; 
		}
		if (!hasName) {
		 id = keywordToken;
		 if (!isConstructor) {
		   SemErr(la, "a method must be given a name (expecting identifier)");
		 }
		}
		
		if (la.kind == 10 || la.kind == 37) {
			if (la.kind == 37) {
				GenericParameters(typeArgs);
			}
			Formals(true, !mmod.IsGhost, ins, out openParen);
			if (la.kind == 35) {
				Get();
				if (isConstructor) { SemErr(t, "constructors cannot have out-parameters"); } 
				Formals(false, !mmod.IsGhost, outs, out openParen);
			}
		} else if (la.kind == 36) {
			Get();
			signatureOmitted = true; openParen = Token.NoToken; 
		} else SynErr(142);
		while (StartOf(8)) {
			MethodSpec(req, mod, ens, dec, ref decAttrs, ref modAttrs);
		}
		if (la.kind == 8) {
			BlockStmt(out body, out bodyStart, out bodyEnd);
		}
		if (isConstructor) {
		 m = new Constructor(id, hasName ? id.val : "_ctor", typeArgs, ins,
		                     req, new Specification<FrameExpression>(mod, modAttrs), ens, new Specification<Expression>(dec, decAttrs), body, attrs, signatureOmitted);
		} else if (isCoMethod) {
		 m = new CoMethod(id, id.val, mmod.IsStatic, typeArgs, ins, outs,
		                req, new Specification<FrameExpression>(mod, modAttrs), ens, new Specification<Expression>(dec, decAttrs), body, attrs, signatureOmitted);
		} else if (isLemma) {
		 m = new Lemma(id, id.val, mmod.IsStatic, typeArgs, ins, outs,
		               req, new Specification<FrameExpression>(mod, modAttrs), ens, new Specification<Expression>(dec, decAttrs), body, attrs, signatureOmitted);
		} else {
		 m = new Method(id, id.val, mmod.IsStatic, mmod.IsGhost, typeArgs, ins, outs,
		                req, new Specification<FrameExpression>(mod, modAttrs), ens, new Specification<Expression>(dec, decAttrs), body, attrs, signatureOmitted);
		}
		m.BodyStartTok = bodyStart;
		m.BodyEndTok = bodyEnd;
		
	}

	void DatatypeMemberDecl(List<DatatypeCtor/*!*/>/*!*/ ctors) {
		Contract.Requires(cce.NonNullElements(ctors));
		Attributes attrs = null;
		IToken/*!*/ id;
		List<Formal/*!*/> formals = new List<Formal/*!*/>();
		
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		NoUSIdent(out id);
		if (la.kind == 10) {
			FormalsOptionalIds(formals);
		}
		ctors.Add(new DatatypeCtor(id, id.val, formals, attrs)); 
	}

	void FormalsOptionalIds(List<Formal/*!*/>/*!*/ formals) {
		Contract.Requires(cce.NonNullElements(formals)); IToken/*!*/ id;  Type/*!*/ ty;  string/*!*/ name;  bool isGhost; 
		Expect(10);
		if (StartOf(9)) {
			TypeIdentOptional(out id, out name, out ty, out isGhost);
			formals.Add(new Formal(id, name, ty, true, isGhost)); 
			while (la.kind == 29) {
				Get();
				TypeIdentOptional(out id, out name, out ty, out isGhost);
				formals.Add(new Formal(id, name, ty, true, isGhost)); 
			}
		}
		Expect(32);
	}

	void FIdentType(out IToken/*!*/ id, out Type/*!*/ ty) {
		Contract.Ensures(Contract.ValueAtReturn(out id) != null); Contract.Ensures(Contract.ValueAtReturn(out ty) != null);
		id = Token.NoToken;
		
		if (la.kind == 1) {
			WildIdent(out id, false);
		} else if (la.kind == 2) {
			Get();
			id = t; 
		} else SynErr(143);
		Expect(6);
		Type(out ty);
	}

	void GIdentType(bool allowGhostKeyword, out IToken/*!*/ id, out Type/*!*/ ty, out bool isGhost) {
		Contract.Ensures(Contract.ValueAtReturn(out id)!=null);
		Contract.Ensures(Contract.ValueAtReturn(out ty)!=null);
		isGhost = false; 
		if (la.kind == 23) {
			Get();
			if (allowGhostKeyword) { isGhost = true; } else { SemErr(t, "formal cannot be declared 'ghost' in this context"); } 
		}
		IdentType(out id, out ty, true);
	}

	void IdentType(out IToken/*!*/ id, out Type/*!*/ ty, bool allowWildcardId) {
		Contract.Ensures(Contract.ValueAtReturn(out id) != null); Contract.Ensures(Contract.ValueAtReturn(out ty) != null);
		WildIdent(out id, allowWildcardId);
		Expect(6);
		Type(out ty);
	}

	void WildIdent(out IToken/*!*/ x, bool allowWildcardId) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); 
		Expect(1);
		x = t; 
		if (x.val.StartsWith("_")) {
		 if (allowWildcardId && x.val.Length == 1) {
		   t.val = "_v" + anonymousIds++;
		 } else {
		   SemErr("cannot declare identifier beginning with underscore");
		 }
		}
		
	}

	void Type(out Type/*!*/ ty) {
		Contract.Ensures(Contract.ValueAtReturn(out ty) != null); IToken/*!*/ tok; 
		TypeAndToken(out tok, out ty);
	}

	void LocalIdentTypeOptional(out VarDecl/*!*/ var, bool isGhost) {
		IToken/*!*/ id;  Type/*!*/ ty;  Type optType = null;
		
		WildIdent(out id, true);
		if (la.kind == 6) {
			Get();
			Type(out ty);
			optType = ty; 
		}
		var = new VarDecl(id, id.val, optType == null ? new InferredTypeProxy() : optType, isGhost); 
	}

	void IdentTypeOptional(out BoundVar/*!*/ var) {
		Contract.Ensures(Contract.ValueAtReturn(out var)!=null); IToken/*!*/ id;  Type/*!*/ ty;  Type optType = null;
		
		WildIdent(out id, true);
		if (la.kind == 6) {
			Get();
			Type(out ty);
			optType = ty; 
		}
		var = new BoundVar(id, id.val, optType == null ? new InferredTypeProxy() : optType); 
	}

	void TypeIdentOptional(out IToken/*!*/ id, out string/*!*/ identName, out Type/*!*/ ty, out bool isGhost) {
		Contract.Ensures(Contract.ValueAtReturn(out id)!=null);
		Contract.Ensures(Contract.ValueAtReturn(out ty)!=null);
		Contract.Ensures(Contract.ValueAtReturn(out identName)!=null);
		string name = null; id = Token.NoToken; ty = null; isGhost = false; 
		if (la.kind == 23) {
			Get();
			isGhost = true; 
		}
		if (StartOf(10)) {
			TypeAndToken(out id, out ty);
			if (la.kind == 6) {
				Get();
				UserDefinedType udt = ty as UserDefinedType;
				if (udt != null && udt.TypeArgs.Count == 0) {
				 name = udt.Name;
				} else {
				 SemErr(id, "invalid formal-parameter name in datatype constructor");
				}
				
				Type(out ty);
			}
		} else if (la.kind == 2) {
			Get();
			id = t; name = id.val;
			Expect(6);
			Type(out ty);
		} else SynErr(144);
		if (name != null) {
		 identName = name;
		} else {
		 identName = "#" + anonymousIds++;
		}
		
	}

	void TypeAndToken(out IToken/*!*/ tok, out Type/*!*/ ty) {
		Contract.Ensures(Contract.ValueAtReturn(out tok)!=null); Contract.Ensures(Contract.ValueAtReturn(out ty) != null); tok = Token.NoToken;  ty = new BoolType();  /*keep compiler happy*/
		List<Type/*!*/>/*!*/ gt;
		
		switch (la.kind) {
		case 51: {
			Get();
			tok = t; 
			break;
		}
		case 52: {
			Get();
			tok = t;  ty = new NatType(); 
			break;
		}
		case 53: {
			Get();
			tok = t;  ty = new IntType(); 
			break;
		}
		case 54: {
			Get();
			tok = t;  gt = new List<Type/*!*/>(); 
			GenericInstantiation(gt);
			if (gt.Count != 1) {
			 SemErr("set type expects exactly one type argument");
			}
			ty = new SetType(gt[0]);
			
			break;
		}
		case 55: {
			Get();
			tok = t;  gt = new List<Type/*!*/>(); 
			GenericInstantiation(gt);
			if (gt.Count != 1) {
			 SemErr("multiset type expects exactly one type argument");
			}
			ty = new MultiSetType(gt[0]);
			
			break;
		}
		case 56: {
			Get();
			tok = t;  gt = new List<Type/*!*/>(); 
			GenericInstantiation(gt);
			if (gt.Count != 1) {
			 SemErr("seq type expects exactly one type argument");
			}
			ty = new SeqType(gt[0]);
			
			break;
		}
		case 57: {
			Get();
			tok = t;  gt = new List<Type/*!*/>(); 
			GenericInstantiation(gt);
			if (gt.Count != 2) {
			 SemErr("map type expects exactly two type arguments");
			}
			else { ty = new MapType(gt[0], gt[1]); }
			
			break;
		}
		case 1: case 4: case 58: {
			ReferenceType(out tok, out ty);
			break;
		}
		default: SynErr(145); break;
		}
	}

	void Formals(bool incoming, bool allowGhostKeyword, List<Formal/*!*/>/*!*/ formals, out IToken openParen) {
		Contract.Requires(cce.NonNullElements(formals)); IToken/*!*/ id;  Type/*!*/ ty;  bool isGhost; 
		Expect(10);
		openParen = t; 
		if (la.kind == 1 || la.kind == 23) {
			GIdentType(allowGhostKeyword, out id, out ty, out isGhost);
			formals.Add(new Formal(id, id.val, ty, incoming, isGhost)); 
			while (la.kind == 29) {
				Get();
				GIdentType(allowGhostKeyword, out id, out ty, out isGhost);
				formals.Add(new Formal(id, id.val, ty, incoming, isGhost)); 
			}
		}
		Expect(32);
	}

	void IteratorSpec(List<FrameExpression/*!*/>/*!*/ reads, List<FrameExpression/*!*/>/*!*/ mod, List<Expression/*!*/> decreases,
List<MaybeFreeExpression/*!*/>/*!*/ req, List<MaybeFreeExpression/*!*/>/*!*/ ens,
List<MaybeFreeExpression/*!*/>/*!*/ yieldReq, List<MaybeFreeExpression/*!*/>/*!*/ yieldEns,
ref Attributes readsAttrs, ref Attributes modAttrs, ref Attributes decrAttrs) {
		Expression/*!*/ e; FrameExpression/*!*/ fe; bool isFree = false; bool isYield = false; Attributes ensAttrs = null;
		
		while (!(StartOf(11))) {SynErr(146); Get();}
		if (la.kind == 49) {
			Get();
			while (IsAttribute()) {
				Attribute(ref readsAttrs);
			}
			if (StartOf(12)) {
				FrameExpression(out fe);
				reads.Add(fe); 
				while (la.kind == 29) {
					Get();
					FrameExpression(out fe);
					reads.Add(fe); 
				}
			}
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(147); Get();}
			Expect(7);
		} else if (la.kind == 44) {
			Get();
			while (IsAttribute()) {
				Attribute(ref modAttrs);
			}
			if (StartOf(12)) {
				FrameExpression(out fe);
				mod.Add(fe); 
				while (la.kind == 29) {
					Get();
					FrameExpression(out fe);
					mod.Add(fe); 
				}
			}
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(148); Get();}
			Expect(7);
		} else if (StartOf(13)) {
			if (la.kind == 45) {
				Get();
				isFree = true; 
			}
			if (la.kind == 50) {
				Get();
				isYield = true; 
			}
			if (la.kind == 46) {
				Get();
				Expression(out e);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(149); Get();}
				Expect(7);
				if (isYield) {
				 yieldReq.Add(new MaybeFreeExpression(e, isFree));
				} else {
				 req.Add(new MaybeFreeExpression(e, isFree));
				}
				
			} else if (la.kind == 47) {
				Get();
				while (IsAttribute()) {
					Attribute(ref ensAttrs);
				}
				Expression(out e);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(150); Get();}
				Expect(7);
				if (isYield) {
				 yieldEns.Add(new MaybeFreeExpression(e, isFree, ensAttrs));
				} else {
				 ens.Add(new MaybeFreeExpression(e, isFree, ensAttrs));
				}
				
			} else SynErr(151);
		} else if (la.kind == 48) {
			Get();
			while (IsAttribute()) {
				Attribute(ref decrAttrs);
			}
			DecreasesList(decreases, false);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(152); Get();}
			Expect(7);
		} else SynErr(153);
	}

	void BlockStmt(out BlockStmt/*!*/ block, out IToken bodyStart, out IToken bodyEnd) {
		Contract.Ensures(Contract.ValueAtReturn(out block) != null);
		List<Statement/*!*/> body = new List<Statement/*!*/>();
		
		Expect(8);
		bodyStart = t; 
		while (StartOf(14)) {
			Stmt(body);
		}
		Expect(9);
		bodyEnd = t;
		block = new BlockStmt(bodyStart, body); 
	}

	void MethodSpec(List<MaybeFreeExpression/*!*/>/*!*/ req, List<FrameExpression/*!*/>/*!*/ mod, List<MaybeFreeExpression/*!*/>/*!*/ ens,
List<Expression/*!*/>/*!*/ decreases, ref Attributes decAttrs, ref Attributes modAttrs) {
		Contract.Requires(cce.NonNullElements(req)); Contract.Requires(cce.NonNullElements(mod)); Contract.Requires(cce.NonNullElements(ens)); Contract.Requires(cce.NonNullElements(decreases));
		Expression/*!*/ e;  FrameExpression/*!*/ fe;  bool isFree = false; Attributes ensAttrs = null;
		
		while (!(StartOf(15))) {SynErr(154); Get();}
		if (la.kind == 44) {
			Get();
			while (IsAttribute()) {
				Attribute(ref modAttrs);
			}
			if (StartOf(12)) {
				FrameExpression(out fe);
				mod.Add(fe); 
				while (la.kind == 29) {
					Get();
					FrameExpression(out fe);
					mod.Add(fe); 
				}
			}
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(155); Get();}
			Expect(7);
		} else if (la.kind == 45 || la.kind == 46 || la.kind == 47) {
			if (la.kind == 45) {
				Get();
				isFree = true; 
			}
			if (la.kind == 46) {
				Get();
				Expression(out e);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(156); Get();}
				Expect(7);
				req.Add(new MaybeFreeExpression(e, isFree)); 
			} else if (la.kind == 47) {
				Get();
				while (IsAttribute()) {
					Attribute(ref ensAttrs);
				}
				Expression(out e);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(157); Get();}
				Expect(7);
				ens.Add(new MaybeFreeExpression(e, isFree, ensAttrs)); 
			} else SynErr(158);
		} else if (la.kind == 48) {
			Get();
			while (IsAttribute()) {
				Attribute(ref decAttrs);
			}
			DecreasesList(decreases, true);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(159); Get();}
			Expect(7);
		} else SynErr(160);
	}

	void FrameExpression(out FrameExpression/*!*/ fe) {
		Contract.Ensures(Contract.ValueAtReturn(out fe) != null);
		Expression/*!*/ e;
		IToken/*!*/ id;
		string fieldName = null;  IToken feTok = null;
		fe = null;
		
		if (StartOf(16)) {
			Expression(out e);
			feTok = e.tok; 
			if (la.kind == 62) {
				Get();
				Ident(out id);
				fieldName = id.val;  feTok = id; 
			}
			fe = new FrameExpression(feTok, e, fieldName); 
		} else if (la.kind == 62) {
			Get();
			Ident(out id);
			fieldName = id.val; 
			fe = new FrameExpression(id, new ImplicitThisExpr(id), fieldName); 
		} else SynErr(161);
	}

	void Expression(out Expression/*!*/ e) {
		EquivExpression(out e);
	}

	void DecreasesList(List<Expression/*!*/> decreases, bool allowWildcard) {
		Expression/*!*/ e; 
		PossiblyWildExpression(out e);
		if (!allowWildcard && e is WildcardExpr) {
		 SemErr(e.tok, "'decreases *' is only allowed on loops and tail-recursive methods");
		} else {
		 decreases.Add(e);
		}
		
		while (la.kind == 29) {
			Get();
			PossiblyWildExpression(out e);
			if (!allowWildcard && e is WildcardExpr) {
			 SemErr(e.tok, "'decreases *' is only allowed on loops and tail-recursive methods");
			} else {
			 decreases.Add(e);
			}
			
		}
	}

	void GenericInstantiation(List<Type/*!*/>/*!*/ gt) {
		Contract.Requires(cce.NonNullElements(gt)); Type/*!*/ ty; 
		Expect(37);
		Type(out ty);
		gt.Add(ty); 
		while (la.kind == 29) {
			Get();
			Type(out ty);
			gt.Add(ty); 
		}
		Expect(38);
	}

	void ReferenceType(out IToken/*!*/ tok, out Type/*!*/ ty) {
		Contract.Ensures(Contract.ValueAtReturn(out tok) != null); Contract.Ensures(Contract.ValueAtReturn(out ty) != null);
		tok = Token.NoToken;  ty = new BoolType();  /*keep compiler happy*/
		List<Type/*!*/>/*!*/ gt;
		List<IToken> path;
		
		if (la.kind == 58) {
			Get();
			tok = t;  ty = new ObjectType(); 
		} else if (la.kind == 4) {
			Get();
			tok = t;  gt = new List<Type/*!*/>(); 
			GenericInstantiation(gt);
			if (gt.Count != 1) {
			 SemErr("array type expects exactly one type argument");
			}
			int dims = 1;
			if (tok.val.Length != 5) {
			 dims = int.Parse(tok.val.Substring(5));
			}
			ty = theBuiltIns.ArrayType(tok, dims, gt[0], true);
			
		} else if (la.kind == 1) {
			Ident(out tok);
			gt = new List<Type/*!*/>();
			path = new List<IToken>(); 
			while (la.kind == 21) {
				path.Add(tok); 
				Get();
				Ident(out tok);
			}
			if (la.kind == 37) {
				GenericInstantiation(gt);
			}
			ty = new UserDefinedType(tok, tok.val, gt, path); 
		} else SynErr(162);
	}

	void FunctionSpec(List<Expression/*!*/>/*!*/ reqs, List<FrameExpression/*!*/>/*!*/ reads, List<Expression/*!*/>/*!*/ ens, List<Expression/*!*/> decreases) {
		Contract.Requires(cce.NonNullElements(reqs));
		Contract.Requires(cce.NonNullElements(reads));
		Contract.Requires(decreases == null || cce.NonNullElements(decreases));
		Expression/*!*/ e;  FrameExpression/*!*/ fe; 
		if (la.kind == 46) {
			while (!(la.kind == 0 || la.kind == 46)) {SynErr(163); Get();}
			Get();
			Expression(out e);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(164); Get();}
			Expect(7);
			reqs.Add(e); 
		} else if (la.kind == 49) {
			Get();
			if (StartOf(17)) {
				PossiblyWildFrameExpression(out fe);
				reads.Add(fe); 
				while (la.kind == 29) {
					Get();
					PossiblyWildFrameExpression(out fe);
					reads.Add(fe); 
				}
			}
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(165); Get();}
			Expect(7);
		} else if (la.kind == 47) {
			Get();
			Expression(out e);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(166); Get();}
			Expect(7);
			ens.Add(e); 
		} else if (la.kind == 48) {
			Get();
			if (decreases == null) {
			 SemErr(t, "'decreases' clauses are meaningless for copredicates, so they are not allowed");
			 decreases = new List<Expression/*!*/>();
			}
			
			DecreasesList(decreases, false);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(167); Get();}
			Expect(7);
		} else SynErr(168);
	}

	void FunctionBody(out Expression/*!*/ e, out IToken bodyStart, out IToken bodyEnd) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null); e = dummyExpr; 
		Expect(8);
		bodyStart = t; 
		ExpressionX(out e);
		Expect(9);
		bodyEnd = t; 
	}

	void PossiblyWildFrameExpression(out FrameExpression/*!*/ fe) {
		Contract.Ensures(Contract.ValueAtReturn(out fe) != null); fe = dummyFrameExpr; 
		if (la.kind == 11) {
			Get();
			fe = new FrameExpression(t, new WildcardExpr(t), null); 
		} else if (StartOf(12)) {
			FrameExpression(out fe);
		} else SynErr(169);
	}

	void PossiblyWildExpression(out Expression/*!*/ e) {
		Contract.Ensures(Contract.ValueAtReturn(out e)!=null);
		e = dummyExpr; 
		if (la.kind == 11) {
			Get();
			e = new WildcardExpr(t); 
		} else if (StartOf(16)) {
			Expression(out e);
		} else SynErr(170);
	}

	void ExpressionX(out Expression/*!*/ e) {
		Expression e0; 
		Expression(out e);
		if (SemiFollowsCall(e)) {
			Expect(7);
			ExpressionX(out e0);
			e = new StmtExpr(e.tok,
			     new UpdateStmt(e.tok, new List<Expression>(), new List<AssignmentRhs>() { new ExprRhs(e, null) }),
			     e0);
			
		}
	}

	void Stmt(List<Statement/*!*/>/*!*/ ss) {
		Statement/*!*/ s;
		
		OneStmt(out s);
		ss.Add(s); 
	}

	void OneStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null); IToken/*!*/ x;  IToken/*!*/ id;  string label = null;
		s = dummyStmt;  /* to please the compiler */
		BlockStmt bs;
		IToken bodyStart, bodyEnd;
		int breakCount;
		
		while (!(StartOf(18))) {SynErr(171); Get();}
		switch (la.kind) {
		case 8: {
			BlockStmt(out bs, out bodyStart, out bodyEnd);
			s = bs; 
			break;
		}
		case 80: {
			AssertStmt(out s);
			break;
		}
		case 69: {
			AssumeStmt(out s);
			break;
		}
		case 81: {
			PrintStmt(out s);
			break;
		}
		case 1: case 2: case 3: case 10: case 27: case 109: case 110: case 111: case 112: case 113: case 114: {
			UpdateStmt(out s);
			break;
		}
		case 23: case 28: {
			VarDeclStatement(out s);
			break;
		}
		case 73: {
			IfStmt(out s);
			break;
		}
		case 77: {
			WhileStmt(out s);
			break;
		}
		case 79: {
			MatchStmt(out s);
			break;
		}
		case 82: case 83: {
			ForallStmt(out s);
			break;
		}
		case 84: {
			CalcStmt(out s);
			break;
		}
		case 63: {
			Get();
			x = t; 
			NoUSIdent(out id);
			Expect(6);
			OneStmt(out s);
			s.Labels = new LList<Label>(new Label(x, id.val), s.Labels); 
			break;
		}
		case 64: {
			Get();
			x = t; breakCount = 1; label = null; 
			if (la.kind == 1) {
				NoUSIdent(out id);
				label = id.val; 
			} else if (la.kind == 7 || la.kind == 64) {
				while (la.kind == 64) {
					Get();
					breakCount++; 
				}
			} else SynErr(172);
			while (!(la.kind == 0 || la.kind == 7)) {SynErr(173); Get();}
			Expect(7);
			s = label != null ? new BreakStmt(x, label) : new BreakStmt(x, breakCount); 
			break;
		}
		case 50: case 67: {
			ReturnStmt(out s);
			break;
		}
		case 36: {
			SkeletonStmt(out s);
			Expect(7);
			break;
		}
		default: SynErr(174); break;
		}
	}

	void AssertStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null); IToken/*!*/ x;
		Expression e = null; Attributes attrs = null;
		
		Expect(80);
		x = t; 
		while (IsAttribute()) {
			Attribute(ref attrs);
		}
		if (StartOf(16)) {
			Expression(out e);
		} else if (la.kind == 36) {
			Get();
		} else SynErr(175);
		Expect(7);
		if (e == null) {
		 s = new SkeletonStatement(new AssertStmt(x, new LiteralExpr(x, true), attrs), true, false);
		} else {
		 s = new AssertStmt(x, e, attrs);
		}
		
	}

	void AssumeStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null); IToken/*!*/ x;
		Expression e = null; Attributes attrs = null;
		
		Expect(69);
		x = t; 
		while (IsAttribute()) {
			Attribute(ref attrs);
		}
		if (StartOf(16)) {
			Expression(out e);
		} else if (la.kind == 36) {
			Get();
		} else SynErr(176);
		if (e == null) {
		 s = new SkeletonStatement(new AssumeStmt(x, new LiteralExpr(x, true), attrs), true, false);
		} else {
		 s = new AssumeStmt(x, e, attrs);
		}
		
		Expect(7);
	}

	void PrintStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null); IToken/*!*/ x;  Attributes.Argument/*!*/ arg;
		List<Attributes.Argument/*!*/> args = new List<Attributes.Argument/*!*/>();
		
		Expect(81);
		x = t; 
		AttributeArg(out arg);
		args.Add(arg); 
		while (la.kind == 29) {
			Get();
			AttributeArg(out arg);
			args.Add(arg); 
		}
		Expect(7);
		s = new PrintStmt(x, args); 
	}

	void UpdateStmt(out Statement/*!*/ s) {
		List<Expression> lhss = new List<Expression>();
		List<AssignmentRhs> rhss = new List<AssignmentRhs>();
		Expression e;  AssignmentRhs r;
		Expression lhs0;
		IToken x;
		Attributes attrs = null;
		IToken suchThatAssume = null;
		Expression suchThat = null;
		
		Lhs(out e);
		x = e.tok; 
		if (la.kind == 7 || la.kind == 8) {
			while (la.kind == 8) {
				Attribute(ref attrs);
			}
			Expect(7);
			rhss.Add(new ExprRhs(e, attrs)); 
		} else if (la.kind == 29 || la.kind == 66 || la.kind == 68) {
			lhss.Add(e);  lhs0 = e; 
			while (la.kind == 29) {
				Get();
				Lhs(out e);
				lhss.Add(e); 
			}
			if (la.kind == 66) {
				Get();
				x = t; 
				Rhs(out r, lhs0);
				rhss.Add(r); 
				while (la.kind == 29) {
					Get();
					Rhs(out r, lhs0);
					rhss.Add(r); 
				}
			} else if (la.kind == 68) {
				Get();
				x = t; 
				if (la.kind == 69) {
					Get();
					suchThatAssume = t; 
				}
				Expression(out suchThat);
			} else SynErr(177);
			Expect(7);
		} else if (la.kind == 6) {
			Get();
			SemErr(t, "invalid statement (did you forget the 'label' keyword?)"); 
		} else SynErr(178);
		if (suchThat != null) {
		 s = new AssignSuchThatStmt(x, lhss, suchThat, suchThatAssume);
		} else {
		 if (lhss.Count == 0 && rhss.Count == 0) {
		   s = new BlockStmt(x, new List<Statement>()); // error, give empty statement
		 } else {
		   s = new UpdateStmt(x, lhss, rhss);
		 }
		}
		
	}

	void VarDeclStatement(out Statement/*!*/ s) {
		IToken x = null, assignTok = null;  bool isGhost = false;
		VarDecl/*!*/ d;
		AssignmentRhs r;  IdentifierExpr lhs0;
		List<VarDecl> lhss = new List<VarDecl>();
		List<AssignmentRhs> rhss = new List<AssignmentRhs>();
		IToken suchThatAssume = null;
		Expression suchThat = null;
		
		if (la.kind == 23) {
			Get();
			isGhost = true;  x = t; 
		}
		Expect(28);
		if (!isGhost) { x = t; } 
		LocalIdentTypeOptional(out d, isGhost);
		lhss.Add(d); 
		while (la.kind == 29) {
			Get();
			LocalIdentTypeOptional(out d, isGhost);
			lhss.Add(d); 
		}
		if (la.kind == 66 || la.kind == 68) {
			if (la.kind == 66) {
				Get();
				assignTok = t;
				lhs0 = new IdentifierExpr(lhss[0].Tok, lhss[0].Name);
				lhs0.Var = lhss[0];  lhs0.Type = lhss[0].OptionalType;  // resolve here
				
				Rhs(out r, lhs0);
				rhss.Add(r); 
				while (la.kind == 29) {
					Get();
					Rhs(out r, lhs0);
					rhss.Add(r); 
				}
			} else {
				Get();
				assignTok = t; 
				if (la.kind == 69) {
					Get();
					suchThatAssume = t; 
				}
				Expression(out suchThat);
			}
		}
		Expect(7);
		ConcreteUpdateStatement update;
		if (suchThat != null) {
		 var ies = new List<Expression>();
		 foreach (var lhs in lhss) {
		   ies.Add(new IdentifierExpr(lhs.Tok, lhs.Name));
		 }
		 update = new AssignSuchThatStmt(assignTok, ies, suchThat, suchThatAssume);
		} else if (rhss.Count == 0) {
		 update = null;
		} else {
		 var ies = new List<Expression>();
		 foreach (var lhs in lhss) {
		   ies.Add(new AutoGhostIdentifierExpr(lhs.Tok, lhs.Name));
		 }
		 update = new UpdateStmt(assignTok, ies, rhss);
		}
		s = new VarDeclStmt(x, lhss, update);
		
	}

	void IfStmt(out Statement/*!*/ ifStmt) {
		Contract.Ensures(Contract.ValueAtReturn(out ifStmt) != null); IToken/*!*/ x;
		Expression guard = null;  bool guardOmitted = false;
		BlockStmt/*!*/ thn;
		BlockStmt/*!*/ bs;
		Statement/*!*/ s;
		Statement els = null;
		IToken bodyStart, bodyEnd;
		List<GuardedAlternative> alternatives;
		ifStmt = dummyStmt;  // to please the compiler
		
		Expect(73);
		x = t; 
		if (IsAlternative()) {
			AlternativeBlock(out alternatives);
			ifStmt = new AlternativeStmt(x, alternatives); 
		} else if (StartOf(19)) {
			if (StartOf(20)) {
				Guard(out guard);
			} else {
				Get();
				guardOmitted = true; 
			}
			BlockStmt(out thn, out bodyStart, out bodyEnd);
			if (la.kind == 74) {
				Get();
				if (la.kind == 73) {
					IfStmt(out s);
					els = s; 
				} else if (la.kind == 8) {
					BlockStmt(out bs, out bodyStart, out bodyEnd);
					els = bs; 
				} else SynErr(179);
			}
			if (guardOmitted) {
			 ifStmt = new SkeletonStatement(new IfStmt(x, guard, thn, els), true, false);
			} else {
			 ifStmt = new IfStmt(x, guard, thn, els);
			}
			
		} else SynErr(180);
	}

	void WhileStmt(out Statement/*!*/ stmt) {
		Contract.Ensures(Contract.ValueAtReturn(out stmt) != null); IToken/*!*/ x;
		Expression guard = null;  bool guardOmitted = false;
		List<MaybeFreeExpression/*!*/> invariants = new List<MaybeFreeExpression/*!*/>();
		List<Expression/*!*/> decreases = new List<Expression/*!*/>();
		Attributes decAttrs = null;
		Attributes modAttrs = null;
		List<FrameExpression/*!*/> mod = null;
		BlockStmt/*!*/ body = null;  bool bodyOmitted = false;
		IToken bodyStart = null, bodyEnd = null;
		List<GuardedAlternative> alternatives;
		stmt = dummyStmt;  // to please the compiler
		
		Expect(77);
		x = t; 
		if (IsLoopSpecOrAlternative()) {
			LoopSpec(out invariants, out decreases, out mod, ref decAttrs, ref modAttrs);
			AlternativeBlock(out alternatives);
			stmt = new AlternativeLoopStmt(x, invariants, new Specification<Expression>(decreases, decAttrs), new Specification<FrameExpression>(mod, modAttrs), alternatives); 
		} else if (StartOf(19)) {
			if (StartOf(20)) {
				Guard(out guard);
				Contract.Assume(guard == null || cce.Owner.None(guard)); 
			} else {
				Get();
				guardOmitted = true; 
			}
			LoopSpec(out invariants, out decreases, out mod, ref decAttrs, ref modAttrs);
			if (la.kind == 8) {
				BlockStmt(out body, out bodyStart, out bodyEnd);
			} else if (la.kind == 36) {
				Get();
				bodyOmitted = true; 
			} else SynErr(181);
			if (guardOmitted || bodyOmitted) {
			 if (mod != null) {
			   SemErr(mod[0].E.tok, "'modifies' clauses are not allowed on refining loops");
			 }
			 if (body == null) {
			   body = new BlockStmt(x, new List<Statement>());
			 }
			 stmt = new WhileStmt(x, guard, invariants, new Specification<Expression>(decreases, decAttrs), new Specification<FrameExpression>(null, null), body);
			 stmt = new SkeletonStatement(stmt, guardOmitted, bodyOmitted);
			} else {
			 // The following statement protects against crashes in case of parsing errors
			 body = body ?? new BlockStmt(x, new List<Statement>());
			 stmt = new WhileStmt(x, guard, invariants, new Specification<Expression>(decreases, decAttrs), new Specification<FrameExpression>(mod, modAttrs), body);
			}
			
		} else SynErr(182);
	}

	void MatchStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null);
		Token x;  Expression/*!*/ e;  MatchCaseStmt/*!*/ c;
		List<MatchCaseStmt/*!*/> cases = new List<MatchCaseStmt/*!*/>(); 
		Expect(79);
		x = t; 
		ExpressionX(out e);
		Expect(8);
		while (la.kind == 75) {
			CaseStatement(out c);
			cases.Add(c); 
		}
		Expect(9);
		s = new MatchStmt(x, e, cases); 
	}

	void ForallStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null);
		IToken/*!*/ x = Token.NoToken;
		bool usesOptionalParen = false;
		List<BoundVar/*!*/> bvars = null;
		Attributes attrs = null;
		Expression range = null;
		var ens = new List<MaybeFreeExpression/*!*/>();
		bool isFree;
		Expression/*!*/ e;
		BlockStmt/*!*/ block;
		IToken bodyStart, bodyEnd;
		
		if (la.kind == 82) {
			Get();
			x = t; 
		} else if (la.kind == 83) {
			Get();
			x = t;
			errors.Warning(t, "the 'parallel' keyword has been deprecated; the comprehension statement now uses the keyword 'forall' (and the parentheses around the bound variables are now optional)");
			
		} else SynErr(183);
		if (la.kind == 10) {
			Get();
			usesOptionalParen = true; 
		}
		if (la.kind == 1) {
			List<BoundVar/*!*/> bvarsX;  Attributes attrsX;  Expression rangeX; 
			QuantifierDomain(out bvarsX, out attrsX, out rangeX);
			bvars = bvarsX; attrs = attrsX; range = rangeX;
			
		}
		if (bvars == null) { bvars = new List<BoundVar>(); }
		if (range == null) { range = new LiteralExpr(x, true); }
		
		if (la.kind == 32) {
			Get();
			if (!usesOptionalParen) { SemErr(t, "found but didn't expect a close parenthesis"); } 
		} else if (la.kind == 8 || la.kind == 45 || la.kind == 47) {
			if (usesOptionalParen) { SemErr(t, "expecting close parenthesis"); } 
		} else SynErr(184);
		while (la.kind == 45 || la.kind == 47) {
			isFree = false; 
			if (la.kind == 45) {
				Get();
				isFree = true; 
			}
			Expect(47);
			Expression(out e);
			Expect(7);
			ens.Add(new MaybeFreeExpression(e, isFree)); 
		}
		BlockStmt(out block, out bodyStart, out bodyEnd);
		s = new ForallStmt(x, bvars, attrs, range, ens, block); 
	}

	void CalcStmt(out Statement/*!*/ s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null);
		Token x;
		CalcStmt.CalcOp/*!*/ op, calcOp = Microsoft.Dafny.CalcStmt.DefaultOp, resOp = Microsoft.Dafny.CalcStmt.DefaultOp;     
		var lines = new List<Expression/*!*/>();
		var hints = new List<BlockStmt/*!*/>(); 
		CalcStmt.CalcOp stepOp;
		var stepOps = new List<CalcStmt.CalcOp>();
		CalcStmt.CalcOp maybeOp;
		Expression/*!*/ e;
		BlockStmt/*!*/ h;
		IToken opTok;
		
		Expect(84);
		x = t; 
		if (StartOf(21)) {
			CalcOp(out opTok, out calcOp);
			maybeOp = calcOp.ResultOp(calcOp); // guard against non-trasitive calcOp (like !=)
			if (maybeOp == null) {
			 SemErr(opTok, "the main operator of a calculation must be transitive");
			}
			resOp = calcOp; 
			
		}
		Expect(8);
		while (StartOf(16)) {
			Expression(out e);
			lines.Add(e); stepOp = calcOp; 
			Expect(7);
			if (StartOf(21)) {
				CalcOp(out opTok, out op);
				maybeOp = resOp.ResultOp(op);
				if (maybeOp == null) {
				 SemErr(opTok, "this operator cannot continue this calculation");
				} else {
				 stepOp = op;
				 resOp = maybeOp;                                                              
				}
				
			}
			stepOps.Add(stepOp); 
			Hint(out h);
			hints.Add(h); 
		}
		Expect(9);
		if (lines.Count > 0) {
		// Repeat the last line to create a dummy line for the dangling hint
		lines.Add(lines[lines.Count - 1]);
		}  
		s = new CalcStmt(x, calcOp, lines, hints, stepOps, resOp); 
		
	}

	void ReturnStmt(out Statement/*!*/ s) {
		IToken returnTok = null;
		List<AssignmentRhs> rhss = null;
		AssignmentRhs r;
		bool isYield = false;
		
		if (la.kind == 67) {
			Get();
			returnTok = t; 
		} else if (la.kind == 50) {
			Get();
			returnTok = t; isYield = true; 
		} else SynErr(185);
		if (StartOf(22)) {
			Rhs(out r, null);
			rhss = new List<AssignmentRhs>(); rhss.Add(r); 
			while (la.kind == 29) {
				Get();
				Rhs(out r, null);
				rhss.Add(r); 
			}
		}
		Expect(7);
		if (isYield) {
		 s = new YieldStmt(returnTok, rhss);
		} else {
		 s = new ReturnStmt(returnTok, rhss);
		}
		
	}

	void SkeletonStmt(out Statement s) {
		List<IToken> names = null;
		List<Expression> exprs = null;
		IToken tok, dotdotdot, whereTok;
		Expression e; 
		Expect(36);
		dotdotdot = t; 
		if (la.kind == 65) {
			Get();
			names = new List<IToken>(); exprs = new List<Expression>(); whereTok = t;
			Ident(out tok);
			names.Add(tok); 
			while (la.kind == 29) {
				Get();
				Ident(out tok);
				names.Add(tok); 
			}
			Expect(66);
			Expression(out e);
			exprs.Add(e); 
			while (la.kind == 29) {
				Get();
				Expression(out e);
				exprs.Add(e); 
			}
			if (exprs.Count != names.Count) {
			 SemErr(whereTok, exprs.Count < names.Count ? "not enough expressions" : "too many expressions");
			 names = null; exprs = null;
			}
			
		}
		s = new SkeletonStatement(dotdotdot, names, exprs); 
	}

	void Rhs(out AssignmentRhs r, Expression receiverForInitCall) {
		Contract.Ensures(Contract.ValueAtReturn<AssignmentRhs>(out r) != null);
		IToken/*!*/ x, newToken;  Expression/*!*/ e;
		Type ty = null;
		List<Expression> ee = null;
		List<Expression> args = null;
		r = dummyRhs;  // to please compiler
		Attributes attrs = null;
		
		if (la.kind == 70) {
			Get();
			newToken = t; 
			TypeAndToken(out x, out ty);
			if (la.kind == 10 || la.kind == 21 || la.kind == 71) {
				if (la.kind == 71) {
					Get();
					ee = new List<Expression>(); 
					Expressions(ee);
					Expect(72);
					UserDefinedType tmp = theBuiltIns.ArrayType(x, ee.Count, new IntType(), true);
					
				} else {
					x = null; args = new List<Expression/*!*/>(); 
					if (la.kind == 21) {
						Get();
						Ident(out x);
					}
					Expect(10);
					if (StartOf(16)) {
						Expressions(args);
					}
					Expect(32);
				}
			}
			if (ee != null) {
			 r = new TypeRhs(newToken, ty, ee);
			} else if (args != null) {
			 r = new TypeRhs(newToken, ty, x == null ? null : x.val, receiverForInitCall, args);
			} else {
			 r = new TypeRhs(newToken, ty);
			}
			
		} else if (la.kind == 11) {
			Get();
			r = new HavocRhs(t); 
		} else if (StartOf(16)) {
			Expression(out e);
			r = new ExprRhs(e); 
		} else SynErr(186);
		while (la.kind == 8) {
			Attribute(ref attrs);
		}
		r.Attributes = attrs; 
	}

	void Lhs(out Expression e) {
		e = dummyExpr;  // the assignment is to please the compiler, the dummy value to satisfy contracts in the event of a parse error
		
		if (la.kind == 1) {
			DottedIdentifiersAndFunction(out e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
		} else if (StartOf(23)) {
			ConstAtomExpression(out e);
			Suffix(ref e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
		} else SynErr(187);
	}

	void Expressions(List<Expression/*!*/>/*!*/ args) {
		Contract.Requires(cce.NonNullElements(args)); Expression/*!*/ e; 
		ExpressionX(out e);
		args.Add(e); 
		while (la.kind == 29) {
			Get();
			ExpressionX(out e);
			args.Add(e); 
		}
	}

	void AlternativeBlock(out List<GuardedAlternative> alternatives) {
		alternatives = new List<GuardedAlternative>();
		IToken x;
		Expression e;
		List<Statement> body;
		
		Expect(8);
		while (la.kind == 75) {
			Get();
			x = t; 
			ExpressionX(out e);
			Expect(76);
			body = new List<Statement>(); 
			while (StartOf(14)) {
				Stmt(body);
			}
			alternatives.Add(new GuardedAlternative(x, e, body)); 
		}
		Expect(9);
	}

	void Guard(out Expression e) {
		Expression/*!*/ ee;  e = null; 
		if (la.kind == 11) {
			Get();
			e = null; 
		} else if (IsParenStar()) {
			Expect(10);
			Expect(11);
			Expect(32);
			e = null; 
		} else if (StartOf(16)) {
			ExpressionX(out ee);
			e = ee; 
		} else SynErr(188);
	}

	void LoopSpec(out List<MaybeFreeExpression/*!*/> invariants, out List<Expression/*!*/> decreases, out List<FrameExpression/*!*/> mod, ref Attributes decAttrs, ref Attributes modAttrs) {
		FrameExpression/*!*/ fe;
		invariants = new List<MaybeFreeExpression/*!*/>();
		MaybeFreeExpression invariant = null;
		decreases = new List<Expression/*!*/>();
		mod = null;
		
		while (StartOf(24)) {
			if (la.kind == 45 || la.kind == 78) {
				Invariant(out invariant);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(189); Get();}
				Expect(7);
				invariants.Add(invariant); 
			} else if (la.kind == 48) {
				while (!(la.kind == 0 || la.kind == 48)) {SynErr(190); Get();}
				Get();
				while (IsAttribute()) {
					Attribute(ref decAttrs);
				}
				DecreasesList(decreases, true);
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(191); Get();}
				Expect(7);
			} else {
				while (!(la.kind == 0 || la.kind == 44)) {SynErr(192); Get();}
				Get();
				while (IsAttribute()) {
					Attribute(ref modAttrs);
				}
				mod = mod ?? new List<FrameExpression>(); 
				if (StartOf(12)) {
					FrameExpression(out fe);
					mod.Add(fe); 
					while (la.kind == 29) {
						Get();
						FrameExpression(out fe);
						mod.Add(fe); 
					}
				}
				while (!(la.kind == 0 || la.kind == 7)) {SynErr(193); Get();}
				Expect(7);
			}
		}
	}

	void Invariant(out MaybeFreeExpression/*!*/ invariant) {
		bool isFree = false; Expression/*!*/ e; List<string> ids = new List<string>(); invariant = null; Attributes attrs = null; 
		while (!(la.kind == 0 || la.kind == 45 || la.kind == 78)) {SynErr(194); Get();}
		if (la.kind == 45) {
			Get();
			isFree = true; 
		}
		Expect(78);
		while (IsAttribute()) {
			Attribute(ref attrs);
		}
		Expression(out e);
		invariant = new MaybeFreeExpression(e, isFree, attrs); 
	}

	void CaseStatement(out MatchCaseStmt/*!*/ c) {
		Contract.Ensures(Contract.ValueAtReturn(out c) != null);
		IToken/*!*/ x, id;
		List<BoundVar/*!*/> arguments = new List<BoundVar/*!*/>();
		BoundVar/*!*/ bv;
		List<Statement/*!*/> body = new List<Statement/*!*/>();
		
		Expect(75);
		x = t; 
		Ident(out id);
		if (la.kind == 10) {
			Get();
			IdentTypeOptional(out bv);
			arguments.Add(bv); 
			while (la.kind == 29) {
				Get();
				IdentTypeOptional(out bv);
				arguments.Add(bv); 
			}
			Expect(32);
		}
		Expect(76);
		while (StartOf(14)) {
			Stmt(body);
		}
		c = new MatchCaseStmt(x, id.val, arguments, body); 
	}

	void AttributeArg(out Attributes.Argument/*!*/ arg) {
		Contract.Ensures(Contract.ValueAtReturn(out arg) != null); Expression/*!*/ e;  arg = dummyAttrArg; 
		if (la.kind == 5) {
			Get();
			arg = new Attributes.Argument(t, t.val.Substring(1, t.val.Length-2)); 
		} else if (StartOf(16)) {
			ExpressionX(out e);
			arg = new Attributes.Argument(t, e); 
		} else SynErr(195);
	}

	void QuantifierDomain(out List<BoundVar/*!*/> bvars, out Attributes attrs, out Expression range) {
		bvars = new List<BoundVar/*!*/>();
		BoundVar/*!*/ bv;
		attrs = null;
		range = null;
		
		IdentTypeOptional(out bv);
		bvars.Add(bv); 
		while (la.kind == 29) {
			Get();
			IdentTypeOptional(out bv);
			bvars.Add(bv); 
		}
		while (IsAttribute()) {
			Attribute(ref attrs);
		}
		if (la.kind == 27) {
			Get();
			Expression(out range);
		}
	}

	void CalcOp(out IToken x, out CalcStmt.CalcOp/*!*/ op) {
		var binOp = BinaryExpr.Opcode.Eq; // Returns Eq if parsing fails because it is compatible with any other operator
		Expression k = null;
		x = null;
		
		switch (la.kind) {
		case 31: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Eq; 
			if (la.kind == 85) {
				Get();
				Expect(71);
				ExpressionX(out k);
				Expect(72);
			}
			break;
		}
		case 37: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Lt; 
			break;
		}
		case 38: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Gt; 
			break;
		}
		case 86: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Le; 
			break;
		}
		case 87: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Ge; 
			break;
		}
		case 88: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Neq; 
			break;
		}
		case 89: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Neq; 
			break;
		}
		case 90: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Le; 
			break;
		}
		case 91: {
			Get();
			x = t;  binOp = BinaryExpr.Opcode.Ge; 
			break;
		}
		case 92: case 93: {
			EquivOp();
			x = t;  binOp = BinaryExpr.Opcode.Iff; 
			break;
		}
		case 94: case 95: {
			ImpliesOp();
			x = t;  binOp = BinaryExpr.Opcode.Imp; 
			break;
		}
		case 96: case 97: {
			ExpliesOp();
			x = t;  binOp = BinaryExpr.Opcode.Exp; 
			break;
		}
		default: SynErr(196); break;
		}
		if (k == null) {
		 op = new Microsoft.Dafny.CalcStmt.BinaryCalcOp(binOp);
		} else {
		 op = new Microsoft.Dafny.CalcStmt.TernaryCalcOp(k);
		}
		
	}

	void Hint(out BlockStmt s) {
		Contract.Ensures(Contract.ValueAtReturn(out s) != null); // returns an empty block statement if the hint is empty
		var subhints = new List<Statement/*!*/>(); 
		IToken bodyStart, bodyEnd;
		BlockStmt/*!*/ block;
		Statement/*!*/ calc;
		Token x = la;
		
		while (la.kind == 8 || la.kind == 84) {
			if (la.kind == 8) {
				BlockStmt(out block, out bodyStart, out bodyEnd);
				subhints.Add(block); 
			} else {
				CalcStmt(out calc);
				subhints.Add(calc); 
			}
		}
		s = new BlockStmt(x, subhints); // if the hint is empty x is the first token of the next line, but it doesn't matter cause the block statement is just used as a container 
		
	}

	void EquivOp() {
		if (la.kind == 92) {
			Get();
		} else if (la.kind == 93) {
			Get();
		} else SynErr(197);
	}

	void ImpliesOp() {
		if (la.kind == 94) {
			Get();
		} else if (la.kind == 95) {
			Get();
		} else SynErr(198);
	}

	void ExpliesOp() {
		if (la.kind == 96) {
			Get();
		} else if (la.kind == 97) {
			Get();
		} else SynErr(199);
	}

	void EquivExpression(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1; 
		ImpliesExpliesExpression(out e0);
		while (la.kind == 92 || la.kind == 93) {
			EquivOp();
			x = t; 
			ImpliesExpliesExpression(out e1);
			e0 = new BinaryExpr(x, BinaryExpr.Opcode.Iff, e0, e1); 
		}
	}

	void ImpliesExpliesExpression(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1; 
		LogicalExpression(out e0);
		if (StartOf(25)) {
			if (la.kind == 94 || la.kind == 95) {
				ImpliesOp();
				x = t; 
				ImpliesExpression(out e1);
				e0 = new BinaryExpr(x, BinaryExpr.Opcode.Imp, e0, e1); 
			} else {
				ExpliesOp();
				x = t; 
				LogicalExpression(out e1);
				e0 = new BinaryExpr(x, BinaryExpr.Opcode.Exp, e0, e1); 
				while (la.kind == 96 || la.kind == 97) {
					ExpliesOp();
					x = t; 
					LogicalExpression(out e1);
					e0 = new BinaryExpr(x, BinaryExpr.Opcode.Exp, e0, e1); 
				}
			}
		}
	}

	void LogicalExpression(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1; 
		RelationalExpression(out e0);
		if (StartOf(26)) {
			if (la.kind == 98 || la.kind == 99) {
				AndOp();
				x = t; 
				RelationalExpression(out e1);
				e0 = new BinaryExpr(x, BinaryExpr.Opcode.And, e0, e1); 
				while (la.kind == 98 || la.kind == 99) {
					AndOp();
					x = t; 
					RelationalExpression(out e1);
					e0 = new BinaryExpr(x, BinaryExpr.Opcode.And, e0, e1); 
				}
			} else {
				OrOp();
				x = t; 
				RelationalExpression(out e1);
				e0 = new BinaryExpr(x, BinaryExpr.Opcode.Or, e0, e1); 
				while (la.kind == 100 || la.kind == 101) {
					OrOp();
					x = t; 
					RelationalExpression(out e1);
					e0 = new BinaryExpr(x, BinaryExpr.Opcode.Or, e0, e1); 
				}
			}
		}
	}

	void ImpliesExpression(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1; 
		LogicalExpression(out e0);
		if (la.kind == 94 || la.kind == 95) {
			ImpliesOp();
			x = t; 
			ImpliesExpression(out e1);
			e0 = new BinaryExpr(x, BinaryExpr.Opcode.Imp, e0, e1); 
		}
	}

	void RelationalExpression(out Expression/*!*/ e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		IToken x, firstOpTok = null;  Expression e0, e1, acc = null;  BinaryExpr.Opcode op;
		List<Expression> chain = null;
		List<BinaryExpr.Opcode> ops = null;
		List<Expression/*?*/> prefixLimits = null;
		Expression k;
		int kind = 0;  // 0 ("uncommitted") indicates chain of ==, possibly with one !=
		              // 1 ("ascending")   indicates chain of ==, <, <=, possibly with one !=
		              // 2 ("descending")  indicates chain of ==, >, >=, possibly with one !=
		              // 3 ("illegal")     indicates illegal chain
		              // 4 ("disjoint")    indicates chain of disjoint set operators
		bool hasSeenNeq = false;
		
		Term(out e0);
		e = e0; 
		if (StartOf(27)) {
			RelOp(out x, out op, out k);
			firstOpTok = x; 
			Term(out e1);
			if (k == null) {
			 e = new BinaryExpr(x, op, e0, e1);
			 if (op == BinaryExpr.Opcode.Disjoint)
			   acc = new BinaryExpr(x, BinaryExpr.Opcode.Add, e0, e1); // accumulate first two operands.
			} else {
			 Contract.Assert(op == BinaryExpr.Opcode.Eq || op == BinaryExpr.Opcode.Neq);
			 e = new TernaryExpr(x, op == BinaryExpr.Opcode.Eq ? TernaryExpr.Opcode.PrefixEqOp : TernaryExpr.Opcode.PrefixNeqOp, k, e0, e1);
			}
			
			while (StartOf(27)) {
				if (chain == null) {
				 chain = new List<Expression>();
				 ops = new List<BinaryExpr.Opcode>();
				 prefixLimits = new List<Expression>();
				 chain.Add(e0);  ops.Add(op);  prefixLimits.Add(k);  chain.Add(e1);
				 switch (op) {
				   case BinaryExpr.Opcode.Eq:
				     kind = 0;  break;
				   case BinaryExpr.Opcode.Neq:
				     kind = 0;  hasSeenNeq = true;  break;
				   case BinaryExpr.Opcode.Lt:
				   case BinaryExpr.Opcode.Le:
				     kind = 1;  break;
				   case BinaryExpr.Opcode.Gt:
				   case BinaryExpr.Opcode.Ge:
				     kind = 2;  break;
				   case BinaryExpr.Opcode.Disjoint:
				     kind = 4;  break;
				   default:
				     kind = 3;  break;
				 }
				}
				e0 = e1;
				
				RelOp(out x, out op, out k);
				switch (op) {
				 case BinaryExpr.Opcode.Eq:
				   if (kind != 0 && kind != 1 && kind != 2) { SemErr(x, "chaining not allowed from the previous operator"); }
				   break;
				 case BinaryExpr.Opcode.Neq:
				   if (hasSeenNeq) { SemErr(x, "a chain cannot have more than one != operator"); }
				   if (kind != 0 && kind != 1 && kind != 2) { SemErr(x, "this operator cannot continue this chain"); }
				   hasSeenNeq = true;  break;
				 case BinaryExpr.Opcode.Lt:
				 case BinaryExpr.Opcode.Le:
				   if (kind == 0) { kind = 1; }
				   else if (kind != 1) { SemErr(x, "this operator chain cannot continue with an ascending operator"); }
				   break;
				 case BinaryExpr.Opcode.Gt:
				 case BinaryExpr.Opcode.Ge:
				   if (kind == 0) { kind = 2; }
				   else if (kind != 2) { SemErr(x, "this operator chain cannot continue with a descending operator"); }
				   break;
				 case BinaryExpr.Opcode.Disjoint:
				   if (kind != 4) { SemErr(x, "can only chain disjoint (!!) with itself."); kind = 3; }
				   break;
				 default:
				   SemErr(x, "this operator cannot be part of a chain");
				   kind = 3;  break;
				}
				
				Term(out e1);
				ops.Add(op); prefixLimits.Add(k); chain.Add(e1);
				if (k != null) {
				 Contract.Assert(op == BinaryExpr.Opcode.Eq || op == BinaryExpr.Opcode.Neq);
				 e = new TernaryExpr(x, op == BinaryExpr.Opcode.Eq ? TernaryExpr.Opcode.PrefixEqOp : TernaryExpr.Opcode.PrefixNeqOp, k, e0, e1);
				} else if (op == BinaryExpr.Opcode.Disjoint) {
				 e = new BinaryExpr(x, BinaryExpr.Opcode.And, e, new BinaryExpr(x, op, acc, e1));
				 acc = new BinaryExpr(x, BinaryExpr.Opcode.Add, acc, e1); //e0 has already been added.
				} else {
				 e = new BinaryExpr(x, BinaryExpr.Opcode.And, e, new BinaryExpr(x, op, e0, e1));
				}
				
			}
		}
		if (chain != null) {
		 e = new ChainingExpression(firstOpTok, chain, ops, prefixLimits, e);
		}
		
	}

	void AndOp() {
		if (la.kind == 98) {
			Get();
		} else if (la.kind == 99) {
			Get();
		} else SynErr(200);
	}

	void OrOp() {
		if (la.kind == 100) {
			Get();
		} else if (la.kind == 101) {
			Get();
		} else SynErr(201);
	}

	void Term(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1;  BinaryExpr.Opcode op; 
		Factor(out e0);
		while (la.kind == 104 || la.kind == 105) {
			AddOp(out x, out op);
			Factor(out e1);
			e0 = new BinaryExpr(x, op, e0, e1); 
		}
	}

	void RelOp(out IToken/*!*/ x, out BinaryExpr.Opcode op, out Expression k) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null);
		x = Token.NoToken;  op = BinaryExpr.Opcode.Add/*(dummy)*/;
		IToken y;
		k = null;
		
		switch (la.kind) {
		case 31: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Eq; 
			if (la.kind == 85) {
				Get();
				Expect(71);
				ExpressionX(out k);
				Expect(72);
			}
			break;
		}
		case 37: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Lt; 
			break;
		}
		case 38: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Gt; 
			break;
		}
		case 86: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Le; 
			break;
		}
		case 87: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Ge; 
			break;
		}
		case 88: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Neq; 
			if (la.kind == 85) {
				Get();
				Expect(71);
				ExpressionX(out k);
				Expect(72);
			}
			break;
		}
		case 102: {
			Get();
			x = t;  op = BinaryExpr.Opcode.In; 
			break;
		}
		case 12: {
			Get();
			x = t;  op = BinaryExpr.Opcode.NotIn; 
			break;
		}
		case 103: {
			Get();
			x = t;  y = Token.NoToken; 
			if (la.kind == 103) {
				Get();
				y = t; 
			}
			if (y == Token.NoToken) {
			 SemErr(x, "invalid RelOp");
			} else if (y.pos != x.pos + 1) {
			 SemErr(x, "invalid RelOp (perhaps you intended \"!!\" with no intervening whitespace?)");
			} else {
			 x.val = "!!";
			 op = BinaryExpr.Opcode.Disjoint;
			}
			
			break;
		}
		case 89: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Neq; 
			break;
		}
		case 90: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Le; 
			break;
		}
		case 91: {
			Get();
			x = t;  op = BinaryExpr.Opcode.Ge; 
			break;
		}
		default: SynErr(202); break;
		}
	}

	void Factor(out Expression/*!*/ e0) {
		Contract.Ensures(Contract.ValueAtReturn(out e0) != null); IToken/*!*/ x;  Expression/*!*/ e1;  BinaryExpr.Opcode op; 
		UnaryExpression(out e0);
		while (la.kind == 11 || la.kind == 106 || la.kind == 107) {
			MulOp(out x, out op);
			UnaryExpression(out e1);
			e0 = new BinaryExpr(x, op, e0, e1); 
		}
	}

	void AddOp(out IToken/*!*/ x, out BinaryExpr.Opcode op) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); x = Token.NoToken;  op=BinaryExpr.Opcode.Add/*(dummy)*/; 
		if (la.kind == 104) {
			Get();
			x = t;  op = BinaryExpr.Opcode.Add; 
		} else if (la.kind == 105) {
			Get();
			x = t;  op = BinaryExpr.Opcode.Sub; 
		} else SynErr(203);
	}

	void UnaryExpression(out Expression/*!*/ e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null); IToken/*!*/ x;  e = dummyExpr; 
		switch (la.kind) {
		case 105: {
			Get();
			x = t; 
			UnaryExpression(out e);
			e = new BinaryExpr(x, BinaryExpr.Opcode.Sub, new LiteralExpr(x, 0), e); 
			break;
		}
		case 103: case 108: {
			NegOp();
			x = t; 
			UnaryExpression(out e);
			e = new UnaryExpr(x, UnaryExpr.Opcode.Not, e); 
			break;
		}
		case 28: case 54: case 63: case 69: case 73: case 79: case 80: case 82: case 84: case 117: case 118: case 119: {
			EndlessExpression(out e);
			break;
		}
		case 1: {
			DottedIdentifiersAndFunction(out e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
			break;
		}
		case 8: case 71: {
			DisplayExpr(out e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
			break;
		}
		case 55: {
			MultiSetExpr(out e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
			break;
		}
		case 57: {
			Get();
			x = t; 
			if (la.kind == 71) {
				MapDisplayExpr(x, out e);
				while (la.kind == 21 || la.kind == 71) {
					Suffix(ref e);
				}
			} else if (la.kind == 1) {
				MapComprehensionExpr(x, out e);
			} else if (StartOf(28)) {
				SemErr("map must be followed by literal in brackets or comprehension."); 
			} else SynErr(204);
			break;
		}
		case 2: case 3: case 10: case 27: case 109: case 110: case 111: case 112: case 113: case 114: {
			ConstAtomExpression(out e);
			while (la.kind == 21 || la.kind == 71) {
				Suffix(ref e);
			}
			break;
		}
		default: SynErr(205); break;
		}
	}

	void MulOp(out IToken/*!*/ x, out BinaryExpr.Opcode op) {
		Contract.Ensures(Contract.ValueAtReturn(out x) != null); x = Token.NoToken;  op = BinaryExpr.Opcode.Add/*(dummy)*/; 
		if (la.kind == 11) {
			Get();
			x = t;  op = BinaryExpr.Opcode.Mul; 
		} else if (la.kind == 106) {
			Get();
			x = t;  op = BinaryExpr.Opcode.Div; 
		} else if (la.kind == 107) {
			Get();
			x = t;  op = BinaryExpr.Opcode.Mod; 
		} else SynErr(206);
	}

	void NegOp() {
		if (la.kind == 103) {
			Get();
		} else if (la.kind == 108) {
			Get();
		} else SynErr(207);
	}

	void EndlessExpression(out Expression e) {
		IToken/*!*/ x;
		Expression e0, e1;
		Statement s;
		e = dummyExpr;
		
		switch (la.kind) {
		case 73: {
			Get();
			x = t; 
			Expression(out e);
			Expect(115);
			Expression(out e0);
			Expect(74);
			Expression(out e1);
			e = new ITEExpr(x, e, e0, e1); 
			break;
		}
		case 79: {
			MatchExpression(out e);
			break;
		}
		case 82: case 117: case 118: case 119: {
			QuantifierGuts(out e);
			break;
		}
		case 54: {
			ComprehensionExpr(out e);
			break;
		}
		case 69: case 80: case 84: {
			StmtInExpr(out s);
			Expression(out e);
			e = new StmtExpr(s.Tok, s, e); 
			break;
		}
		case 28: {
			LetExpr(out e);
			break;
		}
		case 63: {
			NamedExpr(out e);
			break;
		}
		default: SynErr(208); break;
		}
	}

	void DottedIdentifiersAndFunction(out Expression e) {
		IToken id;  IToken openParen = null;
		List<Expression> args = null;
		List<IToken> idents = new List<IToken>();
		
		Ident(out id);
		idents.Add(id); 
		while (la.kind == 21) {
			Get();
			IdentOrDigits(out id);
			idents.Add(id); 
		}
		if (la.kind == 10 || la.kind == 85) {
			args = new List<Expression>(); 
			if (la.kind == 85) {
				Get();
				id.val = id.val + "#";  Expression k; 
				Expect(71);
				ExpressionX(out k);
				Expect(72);
				args.Add(k); 
			}
			Expect(10);
			openParen = t; 
			if (StartOf(16)) {
				Expressions(args);
			}
			Expect(32);
		}
		e = new IdentifierSequence(idents, openParen, args); 
	}

	void Suffix(ref Expression/*!*/ e) {
		Contract.Requires(e != null); Contract.Ensures(e!=null); IToken/*!*/ id, x;  List<Expression/*!*/>/*!*/ args;
		Expression e0 = null;  Expression e1 = null;  Expression/*!*/ ee;  bool anyDots = false;
		List<Expression> multipleIndices = null;
		bool func = false;
		
		if (la.kind == 21) {
			Get();
			IdentOrDigits(out id);
			if (la.kind == 10 || la.kind == 85) {
				args = new List<Expression/*!*/>();  func = true; 
				if (la.kind == 85) {
					Get();
					id.val = id.val + "#";  Expression k; 
					Expect(71);
					ExpressionX(out k);
					Expect(72);
					args.Add(k); 
				}
				Expect(10);
				IToken openParen = t; 
				if (StartOf(16)) {
					Expressions(args);
				}
				Expect(32);
				e = new FunctionCallExpr(id, id.val, e, openParen, args); 
			}
			if (!func) { e = new ExprDotName(id, e, id.val); } 
		} else if (la.kind == 71) {
			Get();
			x = t; 
			if (StartOf(16)) {
				ExpressionX(out ee);
				e0 = ee; 
				if (la.kind == 116) {
					Get();
					anyDots = true; 
					if (StartOf(16)) {
						ExpressionX(out ee);
						e1 = ee; 
					}
				} else if (la.kind == 66) {
					Get();
					ExpressionX(out ee);
					e1 = ee; 
				} else if (la.kind == 29 || la.kind == 72) {
					while (la.kind == 29) {
						Get();
						ExpressionX(out ee);
						if (multipleIndices == null) {
						 multipleIndices = new List<Expression>();
						 multipleIndices.Add(e0);
						}
						multipleIndices.Add(ee);
						
					}
				} else SynErr(209);
			} else if (la.kind == 116) {
				Get();
				anyDots = true; 
				if (StartOf(16)) {
					ExpressionX(out ee);
					e1 = ee; 
				}
			} else SynErr(210);
			if (multipleIndices != null) {
			 e = new MultiSelectExpr(x, e, multipleIndices);
			 // make sure an array class with this dimensionality exists
			 UserDefinedType tmp = theBuiltIns.ArrayType(x, multipleIndices.Count, new IntType(), true);
			} else {
			 if (!anyDots && e0 == null) {
			   /* a parsing error occurred */
			   e0 = dummyExpr;
			 }
			 Contract.Assert(anyDots || e0 != null);
			 if (anyDots) {
			   //Contract.Assert(e0 != null || e1 != null);
			   e = new SeqSelectExpr(x, false, e, e0, e1);
			 } else if (e1 == null) {
			   Contract.Assert(e0 != null);
			   e = new SeqSelectExpr(x, true, e, e0, null);
			 } else {
			   Contract.Assert(e0 != null);
			   e = new SeqUpdateExpr(x, e, e0, e1);
			 }
			}
			
			Expect(72);
		} else SynErr(211);
	}

	void DisplayExpr(out Expression e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		IToken/*!*/ x = null;  List<Expression/*!*/>/*!*/ elements;
		e = dummyExpr;
		
		if (la.kind == 8) {
			Get();
			x = t;  elements = new List<Expression/*!*/>(); 
			if (StartOf(16)) {
				Expressions(elements);
			}
			e = new SetDisplayExpr(x, elements);
			Expect(9);
		} else if (la.kind == 71) {
			Get();
			x = t;  elements = new List<Expression/*!*/>(); 
			if (StartOf(16)) {
				Expressions(elements);
			}
			e = new SeqDisplayExpr(x, elements); 
			Expect(72);
		} else SynErr(212);
	}

	void MultiSetExpr(out Expression e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		IToken/*!*/ x = null;  List<Expression/*!*/>/*!*/ elements;
		e = dummyExpr;
		
		Expect(55);
		x = t; 
		if (la.kind == 8) {
			Get();
			elements = new List<Expression/*!*/>(); 
			if (StartOf(16)) {
				Expressions(elements);
			}
			e = new MultiSetDisplayExpr(x, elements);
			Expect(9);
		} else if (la.kind == 10) {
			Get();
			x = t;  elements = new List<Expression/*!*/>(); 
			ExpressionX(out e);
			e = new MultiSetFormingExpr(x, e); 
			Expect(32);
		} else if (StartOf(29)) {
			SemErr("multiset must be followed by multiset literal or expression to coerce in parentheses."); 
		} else SynErr(213);
	}

	void MapDisplayExpr(IToken/*!*/ mapToken, out Expression e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		List<ExpressionPair/*!*/>/*!*/ elements= new List<ExpressionPair/*!*/>() ;
		e = dummyExpr;
		
		Expect(71);
		if (StartOf(16)) {
			MapLiteralExpressions(out elements);
		}
		e = new MapDisplayExpr(mapToken, elements);
		Expect(72);
	}

	void MapComprehensionExpr(IToken/*!*/ mapToken, out Expression e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		BoundVar/*!*/ bv;
		List<BoundVar/*!*/> bvars = new List<BoundVar/*!*/>();
		Expression range = null;
		Expression body;
		
		IdentTypeOptional(out bv);
		bvars.Add(bv); 
		if (la.kind == 27) {
			Get();
			Expression(out range);
		}
		QSep();
		Expression(out body);
		e = new MapComprehension(mapToken, bvars, range ?? new LiteralExpr(mapToken, true), body);
		
	}

	void ConstAtomExpression(out Expression/*!*/ e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null);
		IToken/*!*/ x;  BigInteger n;
		e = dummyExpr;
		
		switch (la.kind) {
		case 109: {
			Get();
			e = new LiteralExpr(t, false); 
			break;
		}
		case 110: {
			Get();
			e = new LiteralExpr(t, true); 
			break;
		}
		case 111: {
			Get();
			e = new LiteralExpr(t); 
			break;
		}
		case 2: case 3: {
			Nat(out n);
			e = new LiteralExpr(t, n); 
			break;
		}
		case 112: {
			Get();
			e = new ThisExpr(t); 
			break;
		}
		case 113: {
			Get();
			x = t; 
			Expect(10);
			Expression(out e);
			Expect(32);
			e = new FreshExpr(x, e); 
			break;
		}
		case 114: {
			Get();
			x = t; 
			Expect(10);
			Expression(out e);
			Expect(32);
			e = new OldExpr(x, e); 
			break;
		}
		case 27: {
			Get();
			x = t; 
			Expression(out e);
			e = new UnaryExpr(x, UnaryExpr.Opcode.SeqLength, e); 
			Expect(27);
			break;
		}
		case 10: {
			Get();
			x = t; 
			Expression(out e);
			e = new ParensExpression(x, e); 
			Expect(32);
			break;
		}
		default: SynErr(214); break;
		}
	}

	void Nat(out BigInteger n) {
		n = BigInteger.Zero; 
		if (la.kind == 2) {
			Get();
			try {
			 n = BigInteger.Parse(t.val);
			} catch (System.FormatException) {
			 SemErr("incorrectly formatted number");
			 n = BigInteger.Zero;
			}
			
		} else if (la.kind == 3) {
			Get();
			try {
			 // note: leading 0 required when parsing positive hex numbers
			 n = BigInteger.Parse("0" + t.val.Substring(2), System.Globalization.NumberStyles.HexNumber);
			} catch (System.FormatException) {
			 SemErr("incorrectly formatted number");
			 n = BigInteger.Zero;
			}
			
		} else SynErr(215);
	}

	void MapLiteralExpressions(out List<ExpressionPair> elements) {
		Expression/*!*/ d, r;
		elements = new List<ExpressionPair/*!*/>(); 
		ExpressionX(out d);
		Expect(66);
		ExpressionX(out r);
		elements.Add(new ExpressionPair(d,r)); 
		while (la.kind == 29) {
			Get();
			ExpressionX(out d);
			Expect(66);
			ExpressionX(out r);
			elements.Add(new ExpressionPair(d,r)); 
		}
	}

	void QSep() {
		if (la.kind == 120) {
			Get();
		} else if (la.kind == 121) {
			Get();
		} else SynErr(216);
	}

	void MatchExpression(out Expression/*!*/ e) {
		Contract.Ensures(Contract.ValueAtReturn(out e) != null); IToken/*!*/ x;  MatchCaseExpr/*!*/ c;
		List<MatchCaseExpr/*!*/> cases = new List<MatchCaseExpr/*!*/>();
		
		Expect(79);
		x = t; 
		Expression(out e);
		while (la.kind == 75) {
			CaseExpression(out c);
			cases.Add(c); 
		}
		e = new MatchExpr(x, e, cases); 
	}

	void QuantifierGuts(out Expression/*!*/ q) {
		Contract.Ensures(Contract.ValueAtReturn(out q) != null); IToken/*!*/ x = Token.NoToken;
		bool univ = false;
		List<BoundVar/*!*/> bvars;
		Attributes attrs;
		Expression range;
		Expression/*!*/ body;
		
		if (la.kind == 82 || la.kind == 117) {
			Forall();
			x = t;  univ = true; 
		} else if (la.kind == 118 || la.kind == 119) {
			Exists();
			x = t; 
		} else SynErr(217);
		QuantifierDomain(out bvars, out attrs, out range);
		QSep();
		Expression(out body);
		if (univ) {
		 q = new ForallExpr(x, bvars, range, body, attrs);
		} else {
		 q = new ExistsExpr(x, bvars, range, body, attrs);
		}
		
	}

	void ComprehensionExpr(out Expression/*!*/ q) {
		Contract.Ensures(Contract.ValueAtReturn(out q) != null);
		IToken/*!*/ x = Token.NoToken;
		BoundVar/*!*/ bv;
		List<BoundVar/*!*/> bvars = new List<BoundVar/*!*/>();
		Expression/*!*/ range;
		Expression body = null;
		
		Expect(54);
		x = t; 
		IdentTypeOptional(out bv);
		bvars.Add(bv); 
		while (la.kind == 29) {
			Get();
			IdentTypeOptional(out bv);
			bvars.Add(bv); 
		}
		Expect(27);
		Expression(out range);
		if (la.kind == 120 || la.kind == 121) {
			QSep();
			Expression(out body);
		}
		if (body == null && bvars.Count != 1) { SemErr(t, "a set comprehension with more than one bound variable must have a term expression"); }
		q = new SetComprehension(x, bvars, range, body);
		
	}

	void StmtInExpr(out Statement s) {
		s = dummyStmt; 
		if (la.kind == 80) {
			AssertStmt(out s);
		} else if (la.kind == 69) {
			AssumeStmt(out s);
		} else if (la.kind == 84) {
			CalcStmt(out s);
		} else SynErr(218);
	}

	void LetExpr(out Expression e) {
		IToken/*!*/ x;
		e = dummyExpr;
		BoundVar d;
		List<BoundVar> letVars;  List<Expression> letRHSs;
		bool exact = true;
		
		Expect(28);
		x = t;
		letVars = new List<BoundVar>();
		letRHSs = new List<Expression>(); 
		IdentTypeOptional(out d);
		letVars.Add(d); 
		while (la.kind == 29) {
			Get();
			IdentTypeOptional(out d);
			letVars.Add(d); 
		}
		if (la.kind == 66) {
			Get();
		} else if (la.kind == 68) {
			Get();
			exact = false; 
		} else SynErr(219);
		Expression(out e);
		letRHSs.Add(e); 
		while (la.kind == 29) {
			Get();
			Expression(out e);
			letRHSs.Add(e); 
		}
		Expect(7);
		Expression(out e);
		e = new LetExpr(x, letVars, letRHSs, e, exact); 
	}

	void NamedExpr(out Expression e) {
		IToken/*!*/ x, d;
		e = dummyExpr;
		Expression expr;
		
		Expect(63);
		x = t; 
		NoUSIdent(out d);
		Expect(6);
		Expression(out e);
		expr = e;
		e = new NamedExpr(x, d.val, expr); 
	}

	void CaseExpression(out MatchCaseExpr/*!*/ c) {
		Contract.Ensures(Contract.ValueAtReturn(out c) != null); IToken/*!*/ x, id;
		List<BoundVar/*!*/> arguments = new List<BoundVar/*!*/>();
		BoundVar/*!*/ bv;
		Expression/*!*/ body;
		
		Expect(75);
		x = t; 
		Ident(out id);
		if (la.kind == 10) {
			Get();
			IdentTypeOptional(out bv);
			arguments.Add(bv); 
			while (la.kind == 29) {
				Get();
				IdentTypeOptional(out bv);
				arguments.Add(bv); 
			}
			Expect(32);
		}
		Expect(76);
		Expression(out body);
		c = new MatchCaseExpr(x, id.val, arguments, body); 
	}

	void Forall() {
		if (la.kind == 82) {
			Get();
		} else if (la.kind == 117) {
			Get();
		} else SynErr(220);
	}

	void Exists() {
		if (la.kind == 118) {
			Get();
		} else if (la.kind == 119) {
			Get();
		} else SynErr(221);
	}

	void AttributeBody(ref Attributes attrs) {
		string aName;
		List<Attributes.Argument/*!*/> aArgs = new List<Attributes.Argument/*!*/>();
		Attributes.Argument/*!*/ aArg;
		
		Expect(6);
		Expect(1);
		aName = t.val; 
		if (StartOf(30)) {
			AttributeArg(out aArg);
			aArgs.Add(aArg); 
			while (la.kind == 29) {
				Get();
				AttributeArg(out aArg);
				aArgs.Add(aArg); 
			}
		}
		attrs = new Attributes(aName, aArgs, attrs); 
	}



	public void Parse() {
		la = new Token();
		la.val = "";
		Get();
		Dafny();
		Expect(0);

		Expect(0);
	}

	static readonly bool[,]/*!*/ set = {
		{T,T,T,T, x,x,x,T, T,x,T,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,T,T, T,x,x,x, x,T,x,x, T,x,x,T, T,T,T,T, T,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,T, x,T,x,x, x,T,x,x, x,T,T,T, T,T,T,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,x, T,x,x,x, x,x,T,T, T,T,T,x, T,x,T,x, x,T,x,x, x,x,x,T, T,T,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, T,x,x,x, x,x,x,x, x,x,x,T, T,T,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, T,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{T,x,x,x, x,x,x,x, T,T,T,x, x,T,T,x, T,x,x,x, x,x,T,T, T,T,T,x, T,x,T,x, x,T,x,x, x,T,x,T, T,T,T,T, x,x,T,T, T,T,x,x, x,x,x,x, x,x,x,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, T,T,T,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, T,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,T,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,T, x,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,T, T,x,x,x, x,x,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,T,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,T, x,T,x,x, x,T,x,x, x,T,x,T, T,T,T,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x},
		{T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, T,T,T,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,x,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,T,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{T,T,T,T, x,x,x,x, T,x,T,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,T, T,x,x,x, x,x,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,T,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,T, x,T,x,x, x,T,x,x, x,T,x,T, T,T,T,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,x,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,x,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,x, x,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, T,T,T,T, T,T,T,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,T,T,T, x,x,x,x, T,x,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,x,T, x,x,x,x, x,T,T,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x},
		{x,x,T,T, x,x,x,x, x,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,T,T,T, T,T,T,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, T,T,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, T,T,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,x, x,x,x,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,x,x,x, x,T,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, T,T,T,T, x,x,x,x, x,x,x,x, x,x,T,T, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x},
		{x,x,x,x, x,x,x,T, T,T,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,T, T,x,x,x, T,T,T,x, x,x,x,x, T,T,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,T,x, x,x,T,x, x,x,x,x, T,x,T,T, T,x,T,x, x,x,x,x, x,x,T,T, T,T,T,T, T,T,T,T, T,T,T,T, T,T,T,T, T,T,T,T, x,x,x,x, x,x,x,T, T,x,x,x, T,T,x,x},
		{x,x,x,x, x,x,x,T, T,T,x,T, T,x,x,x, x,x,x,x, x,T,x,x, x,x,x,T, x,T,x,T, T,x,x,x, T,T,T,x, x,x,x,x, T,T,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,T,x, x,x,T,x, x,x,x,T, T,x,T,T, T,x,T,x, x,x,x,x, x,x,T,T, T,T,T,T, T,T,T,T, T,T,T,T, T,T,T,T, T,T,T,T, x,x,x,x, x,x,x,T, T,x,x,x, T,T,x,x},
		{x,T,T,T, x,T,x,x, T,x,T,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,T,T, x,T,x,x, x,x,x,T, x,x,x,x, x,T,x,T, x,T,x,x, x,x,x,T, T,x,T,x, T,x,x,x, x,x,x,x, x,x,x,x, x,x,x,x, x,x,x,T, x,T,x,x, T,T,T,T, T,T,T,x, x,T,T,T, x,x,x,x}

	};
} // end Parser


public class Errors {
	public int count = 0;                                    // number of errors detected
	public System.IO.TextWriter/*!*/ errorStream = Console.Out;   // error messages go to this stream
	public string errMsgFormat = "{0}({1},{2}): error: {3}"; // 0=filename, 1=line, 2=column, 3=text
	public string warningMsgFormat = "{0}({1},{2}): warning: {3}"; // 0=filename, 1=line, 2=column, 3=text

	public void SynErr(string filename, int line, int col, int n) {
		SynErr(filename, line, col, GetSyntaxErrorString(n));
	}

	public virtual void SynErr(string filename, int line, int col, string/*!*/ msg) {
		Contract.Requires(msg != null);
		errorStream.WriteLine(errMsgFormat, filename, line, col, msg);
		count++;
	}

	string GetSyntaxErrorString(int n) {
		string s;
		switch (n) {
			case 0: s = "EOF expected"; break;
			case 1: s = "ident expected"; break;
			case 2: s = "digits expected"; break;
			case 3: s = "hexdigits expected"; break;
			case 4: s = "arrayToken expected"; break;
			case 5: s = "string expected"; break;
			case 6: s = "colon expected"; break;
			case 7: s = "semi expected"; break;
			case 8: s = "lbrace expected"; break;
			case 9: s = "rbrace expected"; break;
			case 10: s = "openparen expected"; break;
			case 11: s = "star expected"; break;
			case 12: s = "notIn expected"; break;
			case 13: s = "\"abstract\" expected"; break;
			case 14: s = "\"module\" expected"; break;
			case 15: s = "\"refines\" expected"; break;
			case 16: s = "\"import\" expected"; break;
			case 17: s = "\"opened\" expected"; break;
			case 18: s = "\"=\" expected"; break;
			case 19: s = "\"as\" expected"; break;
			case 20: s = "\"default\" expected"; break;
			case 21: s = "\".\" expected"; break;
			case 22: s = "\"class\" expected"; break;
			case 23: s = "\"ghost\" expected"; break;
			case 24: s = "\"static\" expected"; break;
			case 25: s = "\"datatype\" expected"; break;
			case 26: s = "\"codatatype\" expected"; break;
			case 27: s = "\"|\" expected"; break;
			case 28: s = "\"var\" expected"; break;
			case 29: s = "\",\" expected"; break;
			case 30: s = "\"type\" expected"; break;
			case 31: s = "\"==\" expected"; break;
			case 32: s = "\")\" expected"; break;
			case 33: s = "\"iterator\" expected"; break;
			case 34: s = "\"yields\" expected"; break;
			case 35: s = "\"returns\" expected"; break;
			case 36: s = "\"...\" expected"; break;
			case 37: s = "\"<\" expected"; break;
			case 38: s = "\">\" expected"; break;
			case 39: s = "\"method\" expected"; break;
			case 40: s = "\"lemma\" expected"; break;
			case 41: s = "\"comethod\" expected"; break;
			case 42: s = "\"colemma\" expected"; break;
			case 43: s = "\"constructor\" expected"; break;
			case 44: s = "\"modifies\" expected"; break;
			case 45: s = "\"free\" expected"; break;
			case 46: s = "\"requires\" expected"; break;
			case 47: s = "\"ensures\" expected"; break;
			case 48: s = "\"decreases\" expected"; break;
			case 49: s = "\"reads\" expected"; break;
			case 50: s = "\"yield\" expected"; break;
			case 51: s = "\"bool\" expected"; break;
			case 52: s = "\"nat\" expected"; break;
			case 53: s = "\"int\" expected"; break;
			case 54: s = "\"set\" expected"; break;
			case 55: s = "\"multiset\" expected"; break;
			case 56: s = "\"seq\" expected"; break;
			case 57: s = "\"map\" expected"; break;
			case 58: s = "\"object\" expected"; break;
			case 59: s = "\"function\" expected"; break;
			case 60: s = "\"predicate\" expected"; break;
			case 61: s = "\"copredicate\" expected"; break;
			case 62: s = "\"`\" expected"; break;
			case 63: s = "\"label\" expected"; break;
			case 64: s = "\"break\" expected"; break;
			case 65: s = "\"where\" expected"; break;
			case 66: s = "\":=\" expected"; break;
			case 67: s = "\"return\" expected"; break;
			case 68: s = "\":|\" expected"; break;
			case 69: s = "\"assume\" expected"; break;
			case 70: s = "\"new\" expected"; break;
			case 71: s = "\"[\" expected"; break;
			case 72: s = "\"]\" expected"; break;
			case 73: s = "\"if\" expected"; break;
			case 74: s = "\"else\" expected"; break;
			case 75: s = "\"case\" expected"; break;
			case 76: s = "\"=>\" expected"; break;
			case 77: s = "\"while\" expected"; break;
			case 78: s = "\"invariant\" expected"; break;
			case 79: s = "\"match\" expected"; break;
			case 80: s = "\"assert\" expected"; break;
			case 81: s = "\"print\" expected"; break;
			case 82: s = "\"forall\" expected"; break;
			case 83: s = "\"parallel\" expected"; break;
			case 84: s = "\"calc\" expected"; break;
			case 85: s = "\"#\" expected"; break;
			case 86: s = "\"<=\" expected"; break;
			case 87: s = "\">=\" expected"; break;
			case 88: s = "\"!=\" expected"; break;
			case 89: s = "\"\\u2260\" expected"; break;
			case 90: s = "\"\\u2264\" expected"; break;
			case 91: s = "\"\\u2265\" expected"; break;
			case 92: s = "\"<==>\" expected"; break;
			case 93: s = "\"\\u21d4\" expected"; break;
			case 94: s = "\"==>\" expected"; break;
			case 95: s = "\"\\u21d2\" expected"; break;
			case 96: s = "\"<==\" expected"; break;
			case 97: s = "\"\\u21d0\" expected"; break;
			case 98: s = "\"&&\" expected"; break;
			case 99: s = "\"\\u2227\" expected"; break;
			case 100: s = "\"||\" expected"; break;
			case 101: s = "\"\\u2228\" expected"; break;
			case 102: s = "\"in\" expected"; break;
			case 103: s = "\"!\" expected"; break;
			case 104: s = "\"+\" expected"; break;
			case 105: s = "\"-\" expected"; break;
			case 106: s = "\"/\" expected"; break;
			case 107: s = "\"%\" expected"; break;
			case 108: s = "\"\\u00ac\" expected"; break;
			case 109: s = "\"false\" expected"; break;
			case 110: s = "\"true\" expected"; break;
			case 111: s = "\"null\" expected"; break;
			case 112: s = "\"this\" expected"; break;
			case 113: s = "\"fresh\" expected"; break;
			case 114: s = "\"old\" expected"; break;
			case 115: s = "\"then\" expected"; break;
			case 116: s = "\"..\" expected"; break;
			case 117: s = "\"\\u2200\" expected"; break;
			case 118: s = "\"exists\" expected"; break;
			case 119: s = "\"\\u2203\" expected"; break;
			case 120: s = "\"::\" expected"; break;
			case 121: s = "\"\\u2022\" expected"; break;
			case 122: s = "??? expected"; break;
			case 123: s = "this symbol not expected in SubModuleDecl"; break;
			case 124: s = "invalid SubModuleDecl"; break;
			case 125: s = "this symbol not expected in ClassDecl"; break;
			case 126: s = "this symbol not expected in DatatypeDecl"; break;
			case 127: s = "invalid DatatypeDecl"; break;
			case 128: s = "this symbol not expected in DatatypeDecl"; break;
			case 129: s = "this symbol not expected in ArbitraryTypeDecl"; break;
			case 130: s = "this symbol not expected in IteratorDecl"; break;
			case 131: s = "invalid IteratorDecl"; break;
			case 132: s = "invalid ClassMemberDecl"; break;
			case 133: s = "invalid IdentOrDigits"; break;
			case 134: s = "this symbol not expected in FieldDecl"; break;
			case 135: s = "this symbol not expected in FieldDecl"; break;
			case 136: s = "invalid FunctionDecl"; break;
			case 137: s = "invalid FunctionDecl"; break;
			case 138: s = "invalid FunctionDecl"; break;
			case 139: s = "invalid FunctionDecl"; break;
			case 140: s = "this symbol not expected in MethodDecl"; break;
			case 141: s = "invalid MethodDecl"; break;
			case 142: s = "invalid MethodDecl"; break;
			case 143: s = "invalid FIdentType"; break;
			case 144: s = "invalid TypeIdentOptional"; break;
			case 145: s = "invalid TypeAndToken"; break;
			case 146: s = "this symbol not expected in IteratorSpec"; break;
			case 147: s = "this symbol not expected in IteratorSpec"; break;
			case 148: s = "this symbol not expected in IteratorSpec"; break;
			case 149: s = "this symbol not expected in IteratorSpec"; break;
			case 150: s = "this symbol not expected in IteratorSpec"; break;
			case 151: s = "invalid IteratorSpec"; break;
			case 152: s = "this symbol not expected in IteratorSpec"; break;
			case 153: s = "invalid IteratorSpec"; break;
			case 154: s = "this symbol not expected in MethodSpec"; break;
			case 155: s = "this symbol not expected in MethodSpec"; break;
			case 156: s = "this symbol not expected in MethodSpec"; break;
			case 157: s = "this symbol not expected in MethodSpec"; break;
			case 158: s = "invalid MethodSpec"; break;
			case 159: s = "this symbol not expected in MethodSpec"; break;
			case 160: s = "invalid MethodSpec"; break;
			case 161: s = "invalid FrameExpression"; break;
			case 162: s = "invalid ReferenceType"; break;
			case 163: s = "this symbol not expected in FunctionSpec"; break;
			case 164: s = "this symbol not expected in FunctionSpec"; break;
			case 165: s = "this symbol not expected in FunctionSpec"; break;
			case 166: s = "this symbol not expected in FunctionSpec"; break;
			case 167: s = "this symbol not expected in FunctionSpec"; break;
			case 168: s = "invalid FunctionSpec"; break;
			case 169: s = "invalid PossiblyWildFrameExpression"; break;
			case 170: s = "invalid PossiblyWildExpression"; break;
			case 171: s = "this symbol not expected in OneStmt"; break;
			case 172: s = "invalid OneStmt"; break;
			case 173: s = "this symbol not expected in OneStmt"; break;
			case 174: s = "invalid OneStmt"; break;
			case 175: s = "invalid AssertStmt"; break;
			case 176: s = "invalid AssumeStmt"; break;
			case 177: s = "invalid UpdateStmt"; break;
			case 178: s = "invalid UpdateStmt"; break;
			case 179: s = "invalid IfStmt"; break;
			case 180: s = "invalid IfStmt"; break;
			case 181: s = "invalid WhileStmt"; break;
			case 182: s = "invalid WhileStmt"; break;
			case 183: s = "invalid ForallStmt"; break;
			case 184: s = "invalid ForallStmt"; break;
			case 185: s = "invalid ReturnStmt"; break;
			case 186: s = "invalid Rhs"; break;
			case 187: s = "invalid Lhs"; break;
			case 188: s = "invalid Guard"; break;
			case 189: s = "this symbol not expected in LoopSpec"; break;
			case 190: s = "this symbol not expected in LoopSpec"; break;
			case 191: s = "this symbol not expected in LoopSpec"; break;
			case 192: s = "this symbol not expected in LoopSpec"; break;
			case 193: s = "this symbol not expected in LoopSpec"; break;
			case 194: s = "this symbol not expected in Invariant"; break;
			case 195: s = "invalid AttributeArg"; break;
			case 196: s = "invalid CalcOp"; break;
			case 197: s = "invalid EquivOp"; break;
			case 198: s = "invalid ImpliesOp"; break;
			case 199: s = "invalid ExpliesOp"; break;
			case 200: s = "invalid AndOp"; break;
			case 201: s = "invalid OrOp"; break;
			case 202: s = "invalid RelOp"; break;
			case 203: s = "invalid AddOp"; break;
			case 204: s = "invalid UnaryExpression"; break;
			case 205: s = "invalid UnaryExpression"; break;
			case 206: s = "invalid MulOp"; break;
			case 207: s = "invalid NegOp"; break;
			case 208: s = "invalid EndlessExpression"; break;
			case 209: s = "invalid Suffix"; break;
			case 210: s = "invalid Suffix"; break;
			case 211: s = "invalid Suffix"; break;
			case 212: s = "invalid DisplayExpr"; break;
			case 213: s = "invalid MultiSetExpr"; break;
			case 214: s = "invalid ConstAtomExpression"; break;
			case 215: s = "invalid Nat"; break;
			case 216: s = "invalid QSep"; break;
			case 217: s = "invalid QuantifierGuts"; break;
			case 218: s = "invalid StmtInExpr"; break;
			case 219: s = "invalid LetExpr"; break;
			case 220: s = "invalid Forall"; break;
			case 221: s = "invalid Exists"; break;

			default: s = "error " + n; break;
		}
		return s;
	}

	public void SemErr(IToken/*!*/ tok, string/*!*/ msg) {  // semantic errors
		Contract.Requires(tok != null);
		Contract.Requires(msg != null);
		SemErr(tok.filename, tok.line, tok.col, msg);
	}

	public virtual void SemErr(string filename, int line, int col, string/*!*/ msg) {
		Contract.Requires(msg != null);
		errorStream.WriteLine(errMsgFormat, filename, line, col, msg);
		count++;
	}

	public void Warning(IToken/*!*/ tok, string/*!*/ msg) {  // warnings
		Contract.Requires(tok != null);
		Contract.Requires(msg != null);
		Warning(tok.filename, tok.line, tok.col, msg);
	}

	public virtual void Warning(string filename, int line, int col, string msg) {
		Contract.Requires(msg != null);
		errorStream.WriteLine(warningMsgFormat, filename, line, col, msg);
	}
} // Errors


public class FatalError: Exception {
	public FatalError(string m): base(m) {}
}


}