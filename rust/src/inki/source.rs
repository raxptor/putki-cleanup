use inki::lexer;
use std::boxed::Box;
use std::rc::Rc;
use shared;

pub enum ResolveStatus<T> {
    Resolved(Rc<T>),
    Failed,
    Null
}

pub trait ObjectLoader {
	fn load(&self, path: &str) -> Option<(&str, &lexer::LexedKv)>;
}

pub trait ParseFromKV where Self:Sized + shared::InkiTypeDescriptor {
	fn parse(kv : &lexer::LexedKv, pctx: &InkiPtrContext) -> Self;
	fn parse_with_type(kv : &lexer::LexedKv, pctx: &InkiPtrContext, type_name:&str) -> Self {
		if <Self as shared::InkiTypeDescriptor>::TAG != type_name {
			println!("Mismatched type in parse_with_type {} vs {}", type_name, <Self as shared::InkiTypeDescriptor>::TAG);
		}		
		<Self as ParseFromKV>::parse(kv, pctx)
	}
}

pub trait Tracker {
    fn follow(&self, path:&str);
}

#[derive(Clone)]
pub struct InkiPtrContext
{
    pub tracker: Option<Rc<Tracker>>,
    pub source: Rc<InkiResolver>
}

pub struct InkiResolver {
	loader: Box<ObjectLoader>
}

impl InkiResolver {
	pub fn new(loader:Box<ObjectLoader>) -> Self {
		Self {
			loader: loader
		}
	}
}

impl InkiResolver {
	fn resolve<T>(ctx:&InkiPtrContext, path:&str) -> ResolveStatus<T> where T : ParseFromKV {
		match ctx.source.loader.load(path)
		{
			Some((type_name, data)) => return ResolveStatus::Resolved(Rc::new(<T as ParseFromKV>::parse_with_type(data, ctx, type_name))),
			_ => return ResolveStatus::Failed
		}
	}
}
pub fn resolve_from<T>(ctx: &InkiPtrContext, path:&str) -> ResolveStatus<T> where T : ParseFromKV + 'static
{	
	return InkiResolver::resolve(ctx, path);
}
