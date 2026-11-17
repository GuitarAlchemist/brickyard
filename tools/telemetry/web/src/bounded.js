// A list that keeps only its last `limit` items: the page's history never grows with time.
export class Bounded {
  constructor(limit) {
    if (!Number.isInteger(limit) || limit < 1) throw new RangeError(`limit ${limit}`);
    this.limit = limit;
    this.list = [];
  }

  push(item) {
    this.list.push(item);
    if (this.list.length > this.limit) this.list.splice(0, this.list.length - this.limit);
    return this;
  }

  items() {
    return this.list.slice();
  }

  get size() {
    return this.list.length;
  }
}
